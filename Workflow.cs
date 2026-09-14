// ============================================================================
//  Workflow Engine — 執行核心（確定性區）
//  流程是資料（JSON），不是 prompt。引擎只認 step 的 type / dependsOn / params，
//  真正做事的 handler 由應用層註冊，所以新增 workflow 不用改引擎。
//  MVP：in-process DAG + Task.WhenAll。正式版可換 Durable Task / Temporal，介面不變。
// ============================================================================

using System.Text.Json;
using DecisionAI.Domain;

namespace DecisionAI.Workflow;

public sealed record StepDef(
    string Id,
    string Type,
    string[]? DependsOn = null,
    Dictionary<string, string>? Params = null,
    int MaxRetries = 1,
    int TimeoutSeconds = 60)
{
    public string[] Deps => DependsOn ?? Array.Empty<string>();
    public string P(string key, string fallback = "") => Params?.GetValueOrDefault(key) ?? fallback;
    public int PInt(string key, int fallback) => int.TryParse(P(key), out var v) ? v : fallback;
}

public sealed record WorkflowDefinition(string Name, StepDef[] Steps)
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static WorkflowDefinition FromJson(string json) => JsonSerializer.Deserialize<WorkflowDefinition>(json, Opts)!;

    /// <summary>設計時檢查：ID 唯一、依賴存在、無環。</summary>
    public void Validate()
    {
        var ids = Steps.Select(s => s.Id).ToList();
        if (ids.Distinct().Count() != ids.Count) throw new InvalidOperationException($"workflow {Name}: step id 重複");
        foreach (var s in Steps)
            foreach (var d in s.Deps)
                if (!ids.Contains(d)) throw new InvalidOperationException($"workflow {Name}: {s.Id} 依賴不存在的 {d}");

        var done = new HashSet<string>(); var remaining = Steps.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(s => s.Deps.All(done.Contains)).ToList();
            if (ready.Count == 0) throw new InvalidOperationException($"workflow {Name}: 有環");
            foreach (var r in ready) { done.Add(r.Id); remaining.Remove(r); }
        }
    }
}

public delegate Task StepHandler(StepDef step, DecisionCase c, CancellationToken ct);

public sealed class WorkflowEngine
{
    private readonly Dictionary<string, StepHandler> _handlers = new();

    public void RegisterHandler(string type, StepHandler handler) => _handlers[type] = handler;

    public async Task RunAsync(WorkflowDefinition wf, DecisionCase c, CancellationToken ct = default)
    {
        wf.Validate();
        var done = new HashSet<string>();
        var pending = wf.Steps.ToList();

        while (pending.Count > 0 && !c.Halted)
        {
            var ready = pending.Where(s => s.Deps.All(done.Contains)).ToList();
            c.Add($"[wf] 執行 {string.Join(" + ", ready.Select(r => r.Id))}{(ready.Count > 1 ? "（平行）" : "")}");
            await Task.WhenAll(ready.Select(s => RunStepAsync(s, c, ct)));
            foreach (var s in ready) { done.Add(s.Id); pending.Remove(s); }
        }
        if (c.Halted && pending.Count > 0) c.Add($"[wf] 未執行的步驟：{string.Join(", ", pending.Select(p => p.Id))}");
    }

    private async Task RunStepAsync(StepDef s, DecisionCase c, CancellationToken ct)
    {
        // 預算守衛：確定性，在每一步之前
        var budget = c.Request.Constraints.Budget;
        if (c.CostSpent > budget) { c.Halt($"預算耗盡：{c.CostSpent:F3} > {budget:F3}（於 {s.Id} 之前）"); return; }

        if (!_handlers.TryGetValue(s.Type, out var handler))
        { c.Halt($"沒有註冊 step type '{s.Type}'（step {s.Id}）"); return; }

        for (int attempt = 0; attempt <= s.MaxRetries; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
            try
            {
                await handler(s, c, cts.Token);
                c.StepStatus[s.Id] = attempt == 0 ? "ok" : $"ok(retry {attempt})";
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { c.Add($"[{s.Id}] 逾時（{s.TimeoutSeconds}s），attempt {attempt}"); }
            catch (Exception ex)
            { c.Add($"[{s.Id}] 失敗 attempt {attempt}: {ex.Message}"); }
        }
        c.StepStatus[s.Id] = "failed";
        c.Halt($"步驟 {s.Id} 重試耗盡");
    }
}
