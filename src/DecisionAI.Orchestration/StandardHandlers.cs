// ============================================================================
//  Handlers — 把 workflow 的 step type 接到各模組。這裡就是 Intake 邊界：
//  LLM 輸出 → schema 解析 → 以「agent 的角色」建事件 → Journal 的權限矩陣決定收不收。
//  handler 只讀快照、回傳事件；不直接寫 Journal。
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Decision;
using DecisionAI.Modules.Probability;
using DecisionAI.Modules.Verification;
using DecisionAI.Modules.Workflow;

namespace DecisionAI.Orchestration;

public sealed class StandardHandlers
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly VerifierRegistry _verifiers;
    private readonly IEnsembler _ensembler;
    private readonly IBayesUpdater _bayes;
    private readonly IDecisionEngine _decision;
    private readonly IHumanGateway _human;
    private readonly IInjectionGuard _guard;
    private readonly IRandomSource _rng;
    private readonly IPolicyStore _policy;

    public StandardHandlers(IAgentRegistry registry, AgentRunner runner, VerifierRegistry verifiers, IEnsembler ensembler,
                            IBayesUpdater bayes, IDecisionEngine decision, IHumanGateway human, IInjectionGuard guard,
                            IRandomSource rng, IPolicyStore policy)
    {
        _registry = registry; _runner = runner; _verifiers = verifiers; _ensembler = ensembler; _bayes = bayes;
        _decision = decision; _human = human; _guard = guard; _rng = rng; _policy = policy;
    }

    public void RegisterAll(WorkflowEngine engine)
    {
        engine.RegisterHandler("parallel_agents",  ParallelAgentsAsync);
        engine.RegisterHandler("agent",            SingleAgentAsync);
        engine.RegisterHandler("verify",           VerifyAsync);
        engine.RegisterHandler("probability",      ProbabilityAsync);
        engine.RegisterHandler("decision",         DecisionAsync);
        engine.RegisterHandler("human_checkpoint", HumanCheckpointAsync);
    }

    private static CaseEvent Sys(string stepId, CaseEvent e) => e.By(stepId, "handler", Actors.System);

    // ── parallel_agents：同一角色 N 個 agent 平行 I/O；吸收時依「選擇順序」而非完成順序 ──
    private async Task<IReadOnlyList<CaseEvent>> ParallelAgentsAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string role = step.P("role", "solver");
        int count = step.PInt("count", role == "solver" ? s.Plan!.SolverCount : 2);
        var policy = _policy.Pin(s.PolicyVersion);

        var agents = _registry.Select(new SelectionQuery(role, s.Domain, count), policy);
        if (agents.Count == 0) throw new InvalidOperationException($"沒有可用的 {role} agent（domain={s.Domain}）");

        var events = new List<CaseEvent> { Sys(step.Id, new Noted($"選用 {string.Join(", ", agents.Select(a => a.Spec.AgentId))}")) };
        var (user, flagged) = UserPrompt(s, step);
        if (flagged.Length > 0) events.Add(Sys(step.Id, new Noted($"InjectionGuard 標記可疑證據：{string.Join(",", flagged)}")));

        var runs = await Task.WhenAll(agents.Select((a, i) =>
            _runner.RunAsync(a, new LlmCallKey(s.Id, step.Id, a.Spec.AgentId, 0), role, SystemPrompt(role, step), user,
                             role == "solver" ? 0.4 + 0.3 * i / Math.Max(1, agents.Count - 1) : 0.7, ct)));

        var working = s.Claims.ToList();
        foreach (var run in runs) events.AddRange(Absorb(step, s, run, role, working));   // 依選擇順序 → Claim ID 可重現
        return events;
    }

    private async Task<IReadOnlyList<CaseEvent>> SingleAgentAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string role = step.P("role", "solver");
        string registryRole = role == Actors.StageSolver ? "solver" : role;
        var agent = _registry.Select(new SelectionQuery(registryRole, s.Domain, 1), _policy.Pin(s.PolicyVersion)).FirstOrDefault()
                    ?? throw new InvalidOperationException($"沒有可用的 {registryRole} agent");

        var (user, flagged) = UserPrompt(s, step);
        var events = new List<CaseEvent>();
        if (flagged.Length > 0) events.Add(Sys(step.Id, new Noted($"InjectionGuard 標記可疑證據：{string.Join(",", flagged)}")));
        var run = await _runner.RunAsync(agent, new LlmCallKey(s.Id, step.Id, agent.Spec.AgentId, 0), role, SystemPrompt(role, step), user, 0.5, ct);
        events.AddRange(Absorb(step, s, run, role, s.Claims.ToList()));
        return events;
    }

    private async Task<IReadOnlyList<CaseEvent>> VerifyAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var level = (VerifierLevel)step.PInt("level", 2);
        var vs = _verifiers.AtLevel(level);
        if (vs.Count == 0) return new[] { Sys(step.Id, new Noted($"沒有註冊 {level} 驗證器 → 略過（能力層上限不會提升）")) };

        var events = new List<CaseEvent>();
        foreach (var v in vs)
        {
            var produced = await v.VerifyAsync(s, step.Id, ct);
            events.AddRange(produced);
            var results = produced.OfType<VerificationRecorded>().Select(r => r.Result).ToList();
            events.Add(Sys(step.Id, new Noted($"{v.Id}（{level}）：{results.Count(r => r.Pass == true)} 通過 / {results.Count(r => r.Pass == false)} 未過")));
            foreach (var r in results.Where(r => r.Pass == false || r.Level >= VerifierLevel.L3_ExecutableTest))
                events.Add(Sys(step.Id, new Noted($"  · {r.ClaimId ?? "-"}: {r.Note}")));
        }
        return events;
    }

    private Task<IReadOnlyList<CaseEvent>> ProbabilityAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var events = new List<CaseEvent>();
        if (step.P("mode", "prior") == "prior")
        {
            var prior = _ensembler.Prior(s, _policy.Pin(s.PolicyVersion));
            if (prior.Count == 0) events.Add(Sys(step.Id, new Noted("沒有可進機率引擎的假設（全是意見或無主張）")));
            else events.Add(Sys(step.Id, new BeliefUpdated(prior, "先驗（集成）：" + Beliefs.Render(prior))));
        }
        else
        {
            var b = s.Beliefs;
            foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is not null))
            {
                b = _bayes.Update(b, exp);
                events.Add(Sys(step.Id, new BeliefUpdated(b, $"{exp.Id} 觀察 outcome[{exp.ObservedOutcome}] → 後驗：{Beliefs.Render(b)}")));
            }
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }

    private Task<IReadOnlyList<CaseEvent>> DecisionAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        if (s.Utilities.Length == 0 || s.Beliefs.Count == 0)
            return Task.FromResult<IReadOnlyList<CaseEvent>>(new[] { Sys(step.Id, new Noted("沒有效用矩陣或機率分布 → 不算 EU，交人決定")) });

        var result = _decision.Decide(s.Beliefs, s.Utilities, s.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity), _rng.Fork(s.Id));
        return Task.FromResult<IReadOnlyList<CaseEvent>>(new[]
        {
            Sys(step.Id, new DecisionMade(result)),
            Sys(step.Id, new Noted($"建議 {result.RecommendedAction ?? "（無）"}：{result.Reason}"))
        });
    }

    private async Task<IReadOnlyList<CaseEvent>> HumanCheckpointAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string what = step.P("what", step.Id);
        if (s.Plan is { RequireHumanApproval: false })
            return new[] { Sys(step.Id, new Noted($"計畫不要求人類核准 → 通過（{what}）")) };

        // 呈現順序：證據與最壞情況先、系統建議後（automation bias 防護的最小版）
        var context = ImmutableArray.CreateBuilder<string>();
        context.Add($"問題：{s.Request!.Problem}");
        context.Add($"證據 {s.Evidence.Count} 條；主張 {s.Claims.Count} 條；已通過最高驗證等級 {s.BestPassedLevel()}");
        if (s.Decision is { } d) context.Add($"最壞情況 {d.WorstCase:F0}、最大後悔 {d.MaxRegret:F0}");
        foreach (var e in s.Experiments.Where(e => e.ObservedOutcome is null)) context.Add($"待跑實驗 {e.Id}：{e.Description}");

        var verdict = await _human.RequestAsync(new HumanRequest(s.Id, step.Id, HumanRole.Approver, what, context.ToImmutable()), ct);
        var events = new List<CaseEvent>
        {
            new HumanActed(HumanRole.Approver.ToString(), what, verdict.Approved, verdict.Rationale).By(step.Id, "approver", Actors.Human)
        };
        if (!verdict.Approved) events.Add(Sys(step.Id, new Halted($"人類拒絕：{what}", ImmutableArray<string>.Empty)));   // 引擎補上未執行清單
        return events;
    }

    // ── 提示組裝：證據只進 user prompt，且經 InjectionGuard 中性化 ──
    private static string SystemPrompt(string role, StepDef step) => role switch
    {
        "solver"              => RolePrompts.Solver,
        "experiment_designer" => RolePrompts.ExperimentDesigner,
        Actors.StageSolver    => RolePrompts.StageSolver,
        _                     => throw new InvalidOperationException($"未知的 agent 角色 {role}（fail-closed）")
    };

    private (string User, ImmutableArray<string> Flagged) UserPrompt(CaseState s, StepDef step)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"問題：{s.Request!.Problem}");
        sb.AppendLine($"目標：{s.Request.Goal}");
        if (step.P("task").Length > 0) sb.AppendLine($"本段任務：{step.P("task")}\n輸出契約：{step.P("contract")}");
        var flagged = ImmutableArray<string>.Empty;
        if (s.Evidence.Count > 0)
        {
            var r = _guard.Render(s.Evidence);
            sb.AppendLine().AppendLine(r.Text);
            flagged = r.FlaggedEvidenceIds;
        }
        if (s.Claims.Any(k => k.Kind == ClaimKind.Hypothesis))
            sb.AppendLine("\n目前假設：\n" + string.Join("\n", s.Hypotheses.Select(h => $"{h.Id}: {h.Statement}" + (s.Beliefs.TryGetValue(h.Id, out var p) ? $"（P={p:P0}）" : ""))));
        return (sb.ToString(), flagged);
    }

    // ── 吸收 agent 輸出：解析 → 以 agent 的角色建事件 ──
    private static IEnumerable<CaseEvent> Absorb(StepDef step, CaseState s, AgentRun run, string role, List<Claim> working)
    {
        yield return Sys(step.Id, new AgentRunRecorded(run));
        if (!run.Ok) { yield return Sys(step.Id, new Noted($"{run.AgentId} 失敗：{run.Error}")); yield break; }

        switch (role)
        {
            case "solver":
                var hyps = AgentOutputs.Hypotheses(run.RawOutput);
                if (hyps.Count == 0) yield return Sys(step.Id, new Noted($"{run.AgentId} 輸出無法解析（L2 會視為無主張）"));
                foreach (var h in hyps)
                {
                    string id = ResolveClaimId(working, ClaimKind.Hypothesis, h.Statement);
                    yield return new ClaimProposed(id, ClaimKind.Hypothesis, h.Statement, h.Confidence, h.EvidenceFor, h.EvidenceAgainst)
                        .By(step.Id, run.AgentId, Actors.Solver);
                }
                break;

            case "experiment_designer":
                int n = s.Experiments.Count;
                foreach (var e in AgentOutputs.Experiments(run.RawOutput))
                {
                    var exp = new Experiment($"EXP-{++n}", e.Description, e.Outcomes, e.Likelihoods, run.AgentId, null);
                    yield return new ExperimentPreRegistered(exp).By(step.Id, run.AgentId, Actors.ExperimentDesigner);
                    yield return Sys(step.Id, new Noted($"{exp.Id} 預先登記：{exp.Description}；似然表 " +
                        string.Join(" ", exp.LikelihoodByClaim.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Select(v => v.ToString("F2")))}]"))));
                }
                break;

            case Actors.StageSolver:
                string cid = ResolveClaimId(working, ClaimKind.Candidate, run.RawOutput);
                yield return new ClaimProposed(cid, ClaimKind.Candidate, run.RawOutput, 0.5, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)
                    .By(step.Id, run.AgentId, Actors.StageSolver);
                break;

            default:
                throw new InvalidOperationException($"沒有 {role} 的吸收規則（fail-closed）");
        }
    }

    /// <summary>同一句話由多個 agent 提出 → 合併成一個 Claim；ID 依首次出現順序。Jaccard ≥ 0.6 視為同一主張。</summary>
    private static string ResolveClaimId(List<Claim> working, ClaimKind kind, string statement)
    {
        var tokens = Claim.Tokens(statement);
        var existing = kind == ClaimKind.Hypothesis
            ? working.FirstOrDefault(k => k.Kind == kind && Claim.Jaccard(tokens, Claim.Tokens(k.Statement)) >= 0.6)
            : null;
        if (existing is not null) return existing.Id;

        string id = $"{(kind == ClaimKind.Hypothesis ? "H" : "C")}{working.Count(k => k.Kind == kind) + 1}";
        working.Add(new Claim(id, kind, statement, ImmutableList<Proposal>.Empty, ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty, false));
        return id;
    }
}
