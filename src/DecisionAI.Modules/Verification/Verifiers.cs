// ============================================================================
//  Verification — 最重要的模組。每級把結果回傳成事件；能力層上限只看等級。
//  Rev2 新增：
//    · PreRegistrationGuard —— L4 執行前比對似然 hash，不符即拒跑
//    · CriticContextBuilder —— critic 的輸入只能是 Journal 投影，看不到 solver 的原始輸出
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
        => new VerificationRecorded(r).By(stepId, verifierId, Actors.Machine);
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
            { events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.LocalId, false, 0, $"引用不存在的證據：{string.Join(",", unknown)} → 標記為意見", Disqualifies: true))); continue; }
            if (claim.EvidenceFor.Count == 0)
            { events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.LocalId, false, 0, "沒有任何支持證據 → 標記為意見，不進機率引擎", Disqualifies: true))); continue; }

            double forW = claim.EvidenceFor.Sum(id => _store.Get(id)!.Reliability);
            double againstW = claim.EvidenceAgainst.Sum(id => _store.Get(id)!.Reliability);
            double support = forW / (forW + againstW);
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.LocalId, support >= 0.5, support,
                $"支持 {claim.EvidenceFor.Count} 條 / 反對 {claim.EvidenceAgainst.Count} 條，可靠度加權支持度 {support:F2}")));
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }
}

/// <summary>
/// critic 的上下文只能從 Journal 投影出來（Rev2 §4.4 第二步）。
/// 獨立性來自資訊不對稱，不是模型不同：同模型、同上下文、只加一句「你是批判者」的自我批判
/// 會傾向確認自己。所以這裡是唯一允許組 critic prompt 的地方，而且會自我稽核。
/// </summary>
public static class CriticContextBuilder
{
    public static string Build(CaseState s, IInjectionGuard guard)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"問題：{s.Request!.Problem}");
        sb.AppendLine().AppendLine(guard.Render(s.Evidence).Text);
        sb.AppendLine().AppendLine("主張（由紀錄投影，不含任何人的推理過程）：");
        foreach (var k in s.Claims)
            sb.AppendLine($"{k.LocalId}: {k.Frame.Render()}（支持 {string.Join(",", k.EvidenceFor.OrderBy(x => x))}；反對 {string.Join(",", k.EvidenceAgainst.OrderBy(x => x))}）");
        return sb.ToString();
    }

    /// <summary>
    /// 硬性約束，可被 mutation testing 檢驗（MR-17）：
    /// 把 solver 的原始輸出餵給 critic，必須當場被拒。
    /// </summary>
    public static void AssertNoRawOutput(string prompt, CaseState s)
    {
        foreach (var run in s.AgentRuns.Where(r => r.Role is "solver" or "stage_solver" && r.Ok))
        {
            var raw = run.RawOutput.Trim();
            if (raw.Length < 24) continue;
            // 取原始輸出的一段指紋：出現在 critic prompt 裡就代表 context 沒有重建
            var probe = raw.Substring(0, Math.Min(64, raw.Length));
            if (prompt.Contains(probe, StringComparison.Ordinal))
                throw new ContextRebuildViolationException(
                    $"critic 的輸入含有 {run.AgentId} 的原始輸出 → 角色切換未伴隨 context 重建");
        }
    }
}

public sealed class ContextRebuildViolationException : Exception
{
    public ContextRebuildViolationException(string message) : base(message) { }
}

/// <summary>L1：LLM 批判。Goodhart 防護三道：選擇時排除本案 solver、context 重建、寫入時角色攔截。</summary>
public sealed class LlmCriticVerifier : IVerifier
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly IInjectionGuard _guard;
    private readonly IPolicyStore _policy;
    private readonly Func<CaseState, IInjectionGuard, string> _buildContext;

    public LlmCriticVerifier(IAgentRegistry registry, AgentRunner runner, IInjectionGuard guard, IPolicyStore policy,
                             Func<CaseState, IInjectionGuard, string>? buildContext = null)
    { _registry = registry; _runner = runner; _guard = guard; _policy = policy; _buildContext = buildContext ?? CriticContextBuilder.Build; }

    public string Id => "llm-critic";
    public VerifierLevel Level => VerifierLevel.L1_LlmCritic;

    public async Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        var critic = _registry.Select(new SelectionQuery("critic", s.Domain, 1, s.AgentsUsedAs("solver").ToImmutableHashSet()), _policy.Pin(s.PolicyVersion)).FirstOrDefault()
                     ?? throw new InvalidOperationException("找不到獨立的 critic（所有候選都是本案 solver）");

        string user = _buildContext(s, _guard);
        CriticContextBuilder.AssertNoRawOutput(user, s);          // ← MR-17 的攔截點

        var run = await _runner.RunAsync(critic, new LlmCallKey(s.Id, stepId, critic.Spec.AgentId, 0), "critic", RolePrompts.Critic, user, 0.2, ct);

        var events = new List<CaseEvent> { new AgentRunRecorded(run).By(stepId, "runner", Actors.System) };
        if (!run.Ok) throw new InvalidOperationException("critic 執行失敗：" + run.Error);

        var known = s.Claims.Select(k => k.LocalId).ToHashSet();
        foreach (var cr in AgentOutputs.Critiques(run.RawOutput))
        {
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
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, claim.LocalId, r.Value.Pass, r.Value.Score, r.Value.Note)));
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }
}

/// <summary>
/// 預登記守衛：L4 執行前比對似然 hash。
/// 沒有 hash 前置比對，pre-registration 只是口號——事後改似然就能把任何結果解釋成預期中的。
/// </summary>
public interface IPreRegistrationGuard
{
    void AssertUnmodified(Experiment exp);
}

public sealed class PreRegistrationGuard : IPreRegistrationGuard
{
    public void AssertUnmodified(Experiment exp)
    {
        var actual = exp.RecomputeHash();
        if (!string.Equals(actual, exp.LikelihoodHash, StringComparison.Ordinal))
            throw new PreRegistrationViolationException(
                $"{exp.Id} 的似然表與登記時不符（登記 {exp.LikelihoodHash[..8]}…，現在 {actual[..8]}…）→ 拒跑");
    }
}

/// <summary>Mutation switch：不比對 hash → 事後竄改似然不會被抓到。</summary>
public sealed class NoOpPreRegistrationGuard : IPreRegistrationGuard
{
    public void AssertUnmodified(Experiment exp) { }
}

public sealed class PreRegistrationViolationException : Exception
{
    public PreRegistrationViolationException(string message) : base(message) { }
}

/// <summary>L4：真實實驗。只跑「已登記且 hash 相符」的實驗；結果寫成 ExperimentResult 證據。</summary>
public sealed class ExperimentVerifier : IVerifier
{
    private readonly IEvidenceStore _store;
    private readonly Func<Experiment, CaseState, CancellationToken, Task<int>> _run;
    private readonly IPreRegistrationGuard _guard;

    public ExperimentVerifier(IEvidenceStore store, Func<Experiment, CaseState, CancellationToken, Task<int>> run,
                              IPreRegistrationGuard? guard = null)
    { _store = store; _run = run; _guard = guard ?? new PreRegistrationGuard(); }

    public string Id => "experiment";
    public VerifierLevel Level => VerifierLevel.L4_Experiment;

    public async Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
    {
        var events = new List<CaseEvent>();
        foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is null))
        {
            if (exp.LikelihoodByClaim.Count == 0)
            { events.Add(new Noted($"{exp.Id} 沒有登記似然表 → 不跑（防事後解釋）").By(stepId, Id, Actors.Machine)); continue; }

            _guard.AssertUnmodified(exp);     // ← 執行前比對 hash

            int observed = await _run(exp, s, ct);
            var ev = _store.Admit(new EvidenceDraft(EvidenceSource.ExperimentResult, exp.Id, $"{exp.Description} → 觀察到：{exp.Outcomes[observed]}"));
            events.Add(new ExperimentObserved(exp.Id, observed).By(stepId, Id, Actors.Machine));
            events.Add(new EvidenceAdmitted(ev).By(stepId, Id, Actors.Machine));
            events.Add(Ev.Machine(Id, stepId, new(Id, Level, null, true, 1.0,
                $"{exp.Id} 觀察到 outcome[{observed}]「{exp.Outcomes[observed]}」→ {ev.Id}（似然 hash {exp.LikelihoodHash[..8]}… 比對相符）")));
        }
        return events;
    }
}
