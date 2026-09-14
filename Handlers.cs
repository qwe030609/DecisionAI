// ============================================================================
//  Handlers — 應用層：把 workflow 的 step type 接到各模組。
//  引擎不知道 solver 是什麼；這裡才知道。新增 step type = 在這裡加一個方法並註冊。
// ============================================================================

using DecisionAI.Agents;
using DecisionAI.Decision;
using DecisionAI.Domain;
using DecisionAI.Probability;
using DecisionAI.Store;
using DecisionAI.Verification;
using DecisionAI.Workflow;

namespace DecisionAI.App;

public sealed class StandardHandlers
{
    private readonly AgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly VerifierRegistry _verifiers;
    private readonly EnsembleEngine _ensemble;
    private readonly BayesianEngine _bayes;
    private readonly DecisionEngine _decision;
    private readonly Func<DecisionCase, string, bool> _approve;   // 人類核准（console / 網頁 / Slack）

    public StandardHandlers(AgentRegistry registry, AgentRunner runner, VerifierRegistry verifiers,
                            EnsembleEngine ensemble, BayesianEngine bayes, DecisionEngine decision,
                            Func<DecisionCase, string, bool> approve)
    {
        _registry = registry; _runner = runner; _verifiers = verifiers;
        _ensemble = ensemble; _bayes = bayes; _decision = decision; _approve = approve;
    }

    public void RegisterAll(WorkflowEngine engine)
    {
        engine.RegisterHandler("parallel_agents",  ParallelAgentsAsync);
        engine.RegisterHandler("agent",            SingleAgentAsync);
        engine.RegisterHandler("verify",           VerifyAsync);
        engine.RegisterHandler("probability",      ProbabilityAsync);
        engine.RegisterHandler("decision",         DecisionAsync);
        engine.RegisterHandler("human_checkpoint", HumanCheckpointAsync);
        engine.RegisterHandler("synthesize",       SynthesizeAsync);
    }

    // ── parallel_agents：同一角色 N 個 agent 平行；solver 數量由 Router 的計畫決定 ──
    private async Task ParallelAgentsAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        string role   = step.P("role", "solver");
        string domain = c.Profile!.Domain;
        int count     = step.PInt("count", role == "solver" ? c.Plan!.SolverCount : 2);
        var views     = step.P("views").Split('|', StringSplitOptions.RemoveEmptyEntries);

        var agents = _registry.Select(role, domain, count);
        if (agents.Count == 0) throw new InvalidOperationException($"沒有可用的 {role} agent（domain={domain}）");
        c.Add($"[{step.Id}] 選用 {string.Join(", ", agents.Select(a => a.AgentId))}");

        var runs = await Task.WhenAll(agents.Select((a, i) =>
        {
            string view = views.Length > 0 ? views[i % views.Length] : "";
            return _runner.RunAsync(a, role, SystemPrompt(role, view), UserPrompt(c, step), c, role == "solver" ? 0.4 + 0.3 * i / Math.Max(1, agents.Count - 1) : 0.7, ct);
        }));

        foreach (var run in runs) Absorb(c, run, role);   // 依輸入順序吸收 → Claim ID 可重現
    }

    private async Task SingleAgentAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        string role = step.P("role", "solver");
        var agent = _registry.Select(role, c.Profile!.Domain, 1).FirstOrDefault()
                    ?? throw new InvalidOperationException($"沒有可用的 {role} agent");
        var run = await _runner.RunAsync(agent, role, SystemPrompt(role, step.P("view")), UserPrompt(c, step), c, 0.5, ct);
        Absorb(c, run, role);
    }

    private async Task VerifyAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        var level = (VerifierLevel)step.PInt("level", 2);
        var vs = _verifiers.AtLevel(level);
        if (vs.Count == 0) { c.Add($"[{step.Id}] 沒有註冊 {level} 驗證器 → 略過（信心上限不會提升）"); return; }
        foreach (var v in vs)
        {
            int before = c.Verifications.Count;
            await v.VerifyAsync(c, ct);
            var added = c.Verifications.Skip(before).ToList();
            c.Add($"[{step.Id}] {v.Id}（{level}）：{added.Count(r => r.Pass == true)} 通過 / {added.Count(r => r.Pass == false)} 未過");
            foreach (var r in added.Where(r => r.Pass == false || r.Level >= VerifierLevel.L4_Experiment))
                c.Add($"[{step.Id}]   · {r.ClaimId ?? "-"}: {r.Note}");
        }
    }

    private Task ProbabilityAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        if (step.P("mode", "prior") == "prior")
            _ensemble.SetPrior(c, c.Profile!.Domain, "solver", c.AgentsUsedAs("solver").ToList());
        else
            foreach (var exp in c.Experiments.Where(e => e.ObservedOutcome is not null)) _bayes.Update(c, exp);
        return Task.CompletedTask;
    }

    private Task DecisionAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        if (c.Request.Actions is not { Count: > 0 } || c.Beliefs.Count == 0)
        { c.Add($"[{step.Id}] 沒有效用矩陣或機率分布 → 不算 EU，交人決定"); return Task.CompletedTask; }

        c.Decision = _decision.Decide(c.Beliefs, c.Request.Actions, c.Request.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity),
                                      c.BestPassedLevel());
        c.Add($"[{step.Id}] 建議 {c.Decision.RecommendedAction ?? "（無）"}：{c.Decision.Reason}");
        return Task.CompletedTask;
    }

    private Task HumanCheckpointAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        string what = step.P("what", step.Id);
        if (c.Plan is { RequireHumanApproval: false }) { c.Add($"[{step.Id}] 計畫不要求人類核准 → 通過（{what}）"); return Task.CompletedTask; }
        bool ok = _approve(c, what);
        c.Add($"[{step.Id}] 人類核准「{what}」→ {(ok ? "同意" : "拒絕")}");
        if (!ok) c.Halt($"人類拒絕：{what}");
        return Task.CompletedTask;
    }

    private async Task SynthesizeAsync(StepDef step, DecisionCase c, CancellationToken ct)
    {
        var agent = _registry.Select("synthesizer", "*", 1, exclude: c.AgentsUsedAs("analyst")).FirstOrDefault()
                    ?? throw new InvalidOperationException("沒有獨立的 synthesizer");
        string parts = string.Join("\n---\n", c.Claims.Where(k => k.Kind == ClaimKind.Candidate).Select(k => k.Statement));
        var run = await _runner.RunAsync(agent, "synthesizer", RolePrompts.Synthesizer, parts, c, 0.2, ct);
        c.Memo = run.RawOutput;
        c.Add($"[{step.Id}] 備忘由 {agent.AgentId} 合成（不含方向結論）");
    }

    // ── 提示組裝 ──
    private static string SystemPrompt(string role, string view) => role switch
    {
        "solver"              => RolePrompts.Solver,
        "experiment_designer" => RolePrompts.ExperimentDesigner,
        "analyst"             => RolePrompts.Analyst + (view.Length > 0 ? $" 你的視角：{view}" : ""),
        _                     => RolePrompts.Analyst
    };

    private static string UserPrompt(DecisionCase c, StepDef step)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"問題：{c.Request.Problem}");
        sb.AppendLine($"目標：{c.Request.Goal}");
        if (step.P("task").Length > 0) sb.AppendLine($"本段任務：{step.P("task")}\n輸出契約：{step.P("contract")}");
        if (c.Evidence.Count > 0) sb.AppendLine("\n證據：\n" + EvidenceStore.RenderForPrompt(c.Evidence));
        if (c.Claims.Any(k => k.Kind == ClaimKind.Hypothesis))
            sb.AppendLine("\n目前假設：\n" + string.Join("\n", c.Hypotheses.Select(h => $"{h.Id}: {h.Statement}" + (c.Beliefs.TryGetValue(h.Id, out var p) ? $"（P={p:P0}）" : ""))));
        return sb.ToString();
    }

    // ── 吸收 agent 輸出：合併重複主張、登記實驗 ──
    private static void Absorb(DecisionCase c, AgentRun run, string role)
    {
        if (!run.Ok) { c.Add($"[absorb] {run.AgentId} 失敗：{run.Error}"); return; }
        switch (role)
        {
            case "solver":
                var hyps = AgentOutputs.Hypotheses(run.RawOutput);
                if (hyps.Count == 0) { c.Add($"[absorb] {run.AgentId} 輸出無法解析（L2 會視為無主張）"); }
                foreach (var h in hyps) Upsert(c, ClaimKind.Hypothesis, h.Statement, run.AgentId, h.Confidence, h.EvidenceFor, h.EvidenceAgainst);
                break;

            case "experiment_designer":
                foreach (var e in AgentOutputs.Experiments(run.RawOutput))
                {
                    var exp = new Experiment
                    {
                        Id = $"EXP-{c.Experiments.Count + 1}", Description = e.Description, Outcomes = e.Outcomes,
                        LikelihoodByClaim = e.Likelihoods, DesignedBy = run.AgentId
                    };
                    c.Experiments.Add(exp);
                    c.Add($"[absorb] {exp.Id} 預先登記：{exp.Description}；似然表 " +
                          string.Join(" ", exp.LikelihoodByClaim.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Select(v => v.ToString("F2")))}]")));
                }
                break;

            default:   // analyst / pipeline 段落：文字交付物
                Upsert(c, ClaimKind.Candidate, run.RawOutput, run.AgentId, 0.5, Array.Empty<string>(), Array.Empty<string>());
                break;
        }
    }

    /// <summary>同一句話由多個 agent 提出 → 合併成一個 Claim；ID 依首次出現順序。相似度這裡用詞彙 Jaccard，正式版換向量相似度（中文無空格時等同精確比對）。</summary>
    private static void Upsert(DecisionCase c, ClaimKind kind, string statement, string agent, double conf, string[] evFor, string[] evAgainst)
    {
        var tokens = Claim.Normalize(statement).Split(' ').ToHashSet();
        var existing = c.Claims.FirstOrDefault(k => k.Kind == kind && kind == ClaimKind.Hypothesis &&
            Jaccard(tokens, Claim.Normalize(k.Statement).Split(' ').ToHashSet()) >= 0.6);
        if (existing is null)
        {
            string prefix = kind == ClaimKind.Hypothesis ? "H" : "C";
            existing = new Claim { Id = $"{prefix}{c.Claims.Count(k => k.Kind == kind) + 1}", Kind = kind, Statement = statement };
            c.Claims.Add(existing);
        }
        existing.Proposals.Add(new Proposal(agent, conf));
        foreach (var e in evFor) existing.EvidenceFor.Add(e);
        foreach (var e in evAgainst) existing.EvidenceAgainst.Add(e);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
        => a.Count == 0 && b.Count == 0 ? 1 : a.Intersect(b).Count() / (double)a.Union(b).Count();
}
