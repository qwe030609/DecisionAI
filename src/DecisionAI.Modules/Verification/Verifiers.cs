// ============================================================================
//  Verification — 最重要的模組。每級把結果回傳成事件；能力層上限只看等級。
//  Phase 1：L1 critic、L2 rule、L3 executable、L4 experiment。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;

namespace DecisionAI.Modules.Verification;

public sealed class VerifierRegistry
{
    private readonly List<IVerifier> _verifiers = new();
    public VerifierRegistry Register(IVerifier v) { _verifiers.Add(v); return this; }
    public IReadOnlyList<IVerifier> AtLevel(VerifierLevel l) => _verifiers.Where(v => v.Level == l).ToList();
    public VerifierInventory Inventory() => new(_verifiers.Select(v => v.Level).Distinct().ToImmutableArray());
}

internal static class Ev
{
    public static CaseEvent Machine(string verifierId, string stepId, VerificationResult r)
        => new VerificationRecorded(r) { StepId = stepId, ActorId = verifierId, ActorRole = Actors.Machine };
}

/// <summary>L2：確定性規則。引用必須存在；沒引用任何證據的主張標記為意見；支持度 = 可靠度加權。</summary>
public sealed class RuleVerifier : IVerifier
{
    private readonly IEvidenceStore _store;
    public RuleVerifier(IEvidenceStore store) => _store = store;
    public string Id => "rule";
    public VerifierLevel Level => VerifierLevel.L2_Rule;

    public Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        var events = new List<CaseEvent>();
        foreach (var claim in s.Claims)
        {
            var unknown = claim.EvidenceFor.Concat(claim.EvidenceAgainst).Where(id => !_store.Exists(id)).Distinct().ToList();
            if (unknown.Count > 0)
            { events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.Id, false, 0, $"引用不存在的證據：{string.Join(",", unknown)} → 標記為意見", Disqualifies: true))); continue; }
            if (claim.EvidenceFor.Count == 0)
            { events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.Id, false, 0, "沒有任何支持證據 → 標記為意見，不進機率引擎", Disqualifies: true))); continue; }

            double forW = claim.EvidenceFor.Sum(id => _store.Get(id)!.Reliability);
            double againstW = claim.EvidenceAgainst.Sum(id => _store.Get(id)!.Reliability);
            double support = forW / (forW + againstW);
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.Id, support >= 0.5, support,
                $"支持 {claim.EvidenceFor.Count} 條 / 反對 {claim.EvidenceAgainst.Count} 條，可靠度加權支持度 {support:F2}")));
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }
}

/// <summary>L1：LLM 批判。Goodhart 防護兩道：選擇時排除本案 solver；寫入時角色為 critic，只能寫 L1 事件。</summary>
public sealed class LlmCriticVerifier : IVerifier
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly IInjectionGuard _guard;
    private readonly IPolicyStore _policy;

    public LlmCriticVerifier(IAgentRegistry registry, AgentRunner runner, IInjectionGuard guard, IPolicyStore policy)
    { _registry = registry; _runner = runner; _guard = guard; _policy = policy; }

    public string Id => "llm-critic";
    public VerifierLevel Level => VerifierLevel.L1_LlmCritic;

    public async Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        // 讀本案 pin 住的 snapshot，不讀 Current（D7）
        var critic = _registry.Select(new SelectionQuery("critic", s.Domain, 1, s.AgentsUsedAs("solver").ToImmutableHashSet()), _policy.Pin(s.PolicyVersion)).FirstOrDefault()
                     ?? throw new InvalidOperationException("找不到獨立的 critic（所有候選都是本案 solver）");

        string user = $"問題：{s.Request!.Problem}\n\n{_guard.Render(s.Evidence).Text}\n\n主張：\n" +
                      string.Join("\n", s.Claims.Select(k => $"{k.Id}: {k.Statement}（支持 {string.Join(",", k.EvidenceFor)}；反對 {string.Join(",", k.EvidenceAgainst)}）"));
        var run = await _runner.RunAsync(critic, new LlmCallKey(s.Id, stepId, critic.Spec.AgentId, 0), "critic", RolePrompts.Critic, user, 0.2, ct);

        var events = new List<CaseEvent> { new AgentRunRecorded(run) { StepId = stepId, ActorId = "runner", ActorRole = Actors.System } };
        if (!run.Ok) throw new InvalidOperationException("critic 執行失敗：" + run.Error);

        var known = s.Claims.Select(k => k.Id).ToHashSet();
        foreach (var cr in AgentOutputs.Critiques(run.RawOutput))
        {
            // Intake 邊界的 schema 半邊：引用不存在的主張 → 丟掉並記錄，不進 Journal
            if (!known.Contains(cr.ClaimId))
            { events.Add(new Noted($"critic 引用不存在的主張 {cr.ClaimId} → 忽略").By(stepId, critic.Spec.AgentId, Actors.Machine)); continue; }
            events.Add(new VerificationRecorded(new($"{Id}:{critic.Spec.AgentId}", Level, cr.ClaimId, cr.Severity < 0.5, 1 - cr.Severity, cr.Issue))
                       .By(stepId, critic.Spec.AgentId, Actors.Critic));
        }
        return events;
    }
}

/// <summary>L3：可執行測試（編譯 / 單元測試 / property test / 模擬器）。test 回傳 null 表示不適用該主張。</summary>
public sealed class ExecutableTestVerifier : IVerifier
{
    private readonly Func<Claim, CaseState, (bool Pass, double Score, string Note)?> _test;
    public ExecutableTestVerifier(string id, Func<Claim, CaseState, (bool, double, string)?> test) { Id = id; _test = test; }
    public string Id { get; }
    public VerifierLevel Level => VerifierLevel.L3_ExecutableTest;

    public Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        var events = new List<CaseEvent>();
        foreach (var claim in s.Claims)
        {
            var r = _test(claim, s);
            if (r is null) continue;
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.Id, r.Value.Pass, r.Value.Score, r.Value.Note)));
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }
}

/// <summary>L4：真實實驗。只跑「已預先登記似然表」的實驗；結果寫成 ExperimentResult 證據。</summary>
public sealed class ExperimentVerifier : IVerifier
{
    private readonly IEvidenceStore _store;
    private readonly Func<Experiment, CaseState, CancellationToken, Task<int>> _run;
    public ExperimentVerifier(IEvidenceStore store, Func<Experiment, CaseState, CancellationToken, Task<int>> run) { _store = store; _run = run; }
    public string Id => "experiment";
    public VerifierLevel Level => VerifierLevel.L4_Experiment;

    public async Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        var events = new List<CaseEvent>();
        foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is null))
        {
            if (exp.LikelihoodByClaim.Count == 0)
            { events.Add(new Noted($"{exp.Id} 沒有預先登記的似然表 → 不跑（防事後解釋）") { StepId = stepId, ActorId = Id, ActorRole = Actors.Machine }); continue; }

            int observed = await _run(exp, s, ct);
            var ev = _store.Admit(new EvidenceDraft(EvidenceSource.ExperimentResult, exp.Id, $"{exp.Description} → 觀察到：{exp.Outcomes[observed]}"));
            events.Add(new ExperimentObserved(exp.Id, observed) { StepId = stepId, ActorId = Id, ActorRole = Actors.Machine });
            events.Add(new EvidenceAdmitted(ev) { StepId = stepId, ActorId = Id, ActorRole = Actors.Machine });
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, null, true, 1.0, $"{exp.Id} 觀察到 outcome[{observed}]「{exp.Outcomes[observed]}」→ {ev.Id}")));
        }
        return events;
    }
}
