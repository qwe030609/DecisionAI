// ============================================================================
//  Workflow Engine — 執行核心（確定性區）。
//  平行只發生在 I/O：handler 讀狀態快照、回傳事件；引擎依宣告順序序列化寫入 Journal。
//  Fail-closed：未註冊 step type → 拒絕整個 DAG；timeout 依 TimeoutPolicy 處置。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;

namespace DecisionAI.Modules.Workflow;

public delegate Task<IReadOnlyList<CaseEvent>> StepHandler(StepDef step, CaseState state, CancellationToken ct);

public interface IWorkflowEngine
{
    Task RunAsync(WorkflowDefinition wf, ICaseJournal journal, CancellationToken ct = default);
}

public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly Dictionary<string, StepHandler> _handlers = new();

    public WorkflowEngine RegisterHandler(string type, StepHandler handler) { _handlers[type] = handler; return this; }

    public async Task RunAsync(WorkflowDefinition wf, ICaseJournal j, CancellationToken ct = default)
    {
        // ── 設計時檢查 + fail-closed：任何一步沒有 handler，整個 DAG 不執行 ──
        wf.Validate();
        var unregistered = wf.Steps.Where(s => !_handlers.ContainsKey(s.Type)).Select(s => $"{s.Id}:{s.Type}").ToList();
        if (unregistered.Count > 0)
            throw new WorkflowRejectedException($"未註冊的 step type → 拒絕執行整個 workflow {wf.Name}：{string.Join(", ", unregistered)}");

        var done = new HashSet<string>();
        var pending = wf.Steps.ToList();

        while (pending.Count > 0 && !j.State.Halted)
        {
            var ready = pending.Where(s => s.Deps.All(done.Contains)).ToList();
            j.ApplyOrThrow(Sys("wf", new Noted($"執行 {string.Join(" + ", ready.Select(r => r.Id))}{(ready.Count > 1 ? "（平行）" : "")}")));

            // 預算守衛：確定性，在這一批之前
            var budget = j.State.Request!.Constraints.Budget;
            if (j.State.CostSpent > budget)
            {
                Halt(j, $"預算耗盡：{j.State.CostSpent:F3} > {budget:F3}（於 {ready[0].Id} 之前）", pending);
                return;
            }

            var snapshot = j.State;                                       // 平行步驟共用同一份快照
            var results = await Task.WhenAll(ready.Select(s => RunStepAsync(s, snapshot, ct)));

            // 依宣告順序序列化寫入（與完成順序無關）
            foreach (var (step, r) in ready.Zip(results))
            {
                done.Add(step.Id); pending.Remove(step);
                foreach (var e in r.Events)
                {
                    // handler 要求中止（例如人類拒絕）：由引擎補上「未執行的步驟清單」，下游不執行
                    if (e is Halted h) { j.ApplyOrThrow(h with { SkippedSteps = pending.Select(p => p.Id).ToImmutableArray() }); return; }
                    j.ApplyOrThrow(e);
                }
                foreach (var e in r.Control) j.ApplyOrThrow(e);
                if (r.HaltReason is not null) { Halt(j, r.HaltReason, pending); return; }
            }
        }
    }

    private sealed record StepOutcome(IReadOnlyList<CaseEvent> Events, IReadOnlyList<CaseEvent> Control, string? HaltReason);

    private async Task<StepOutcome> RunStepAsync(StepDef s, CaseState snapshot, CancellationToken ct)
    {
        var handler = _handlers[s.Type];
        var policy = s.TimeoutOrDefault;
        var control = new List<CaseEvent>();

        for (int attempt = 0; attempt <= s.MaxRetries; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(policy.Seconds));
            try
            {
                var events = await handler(s, snapshot, cts.Token);
                control.Add(Sys(s.Id, new StepCompleted(attempt == 0 ? "ok" : $"ok(retry {attempt})")));
                return new StepOutcome(events, control, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                control.Add(Sys(s.Id, new Noted($"逾時（{policy.Seconds}s），attempt {attempt}")));
                if (policy.OnTimeout == TimeoutAction.DegradeWithPartial)
                {
                    // 降級：記錄部分結果事件，能力層必須據此降級；不重試、不當成完整結果
                    control.Add(Sys(s.Id, new PartialResult($"逾時後以部分結果繼續（{policy.Seconds}s）")));
                    control.Add(Sys(s.Id, new StepCompleted("partial")));
                    return new StepOutcome(Array.Empty<CaseEvent>(), control, null);
                }
            }
            catch (JournalWriteRejectedException ex)
            {
                // 越權寫入不是可重試的錯誤：立即中止
                control.Add(Sys(s.Id, new StepCompleted("failed")));
                return new StepOutcome(Array.Empty<CaseEvent>(), control, $"步驟 {s.Id} 權限拒絕：{ex.Message}");
            }
            catch (Exception ex)
            {
                control.Add(Sys(s.Id, new Noted($"失敗 attempt {attempt}: {ex.Message}")));
            }
        }
        control.Add(Sys(s.Id, new StepCompleted("failed")));
        return new StepOutcome(Array.Empty<CaseEvent>(), control, $"步驟 {s.Id} 重試耗盡");
    }

    private static void Halt(ICaseJournal j, string reason, List<StepDef> skipped)
        => j.ApplyOrThrow(Sys("wf", new Halted(reason, skipped.Select(p => p.Id).ToImmutableArray())));

    private static CaseEvent Sys(string stepId, CaseEvent e) => e with { StepId = stepId, ActorId = "workflow", ActorRole = Actors.System };
}
