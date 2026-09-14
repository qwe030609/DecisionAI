// ============================================================================
//  Verification — 最重要的模組。
//  LLM Critic 只是五級裡最弱的一級。每級都把結果寫進 Case.Verifications，
//  信心上限與 Agent 權重更新都只看等級，不看誰講得漂亮。
// ============================================================================

using DecisionAI.Agents;
using DecisionAI.Domain;
using DecisionAI.Store;

namespace DecisionAI.Verification;

public interface IVerifier
{
    string Id { get; }
    VerifierLevel Level { get; }
    Task VerifyAsync(DecisionCase c, CancellationToken ct = default);
}

public sealed class VerifierRegistry
{
    private readonly List<IVerifier> _verifiers = new();
    public void Register(IVerifier v) => _verifiers.Add(v);
    public IReadOnlyList<IVerifier> AtLevel(VerifierLevel l) => _verifiers.Where(v => v.Level == l).ToList();
    public VerifierLevel BestAvailable() => _verifiers.Count == 0 ? VerifierLevel.L1_LlmCritic : _verifiers.Max(v => v.Level);
}

/// <summary>L2：確定性規則。引用必須存在；沒引用任何證據的主張標記為意見；支持度 = 可靠度加權。</summary>
public sealed class RuleVerifier : IVerifier
{
    private readonly EvidenceStore _store;
    public RuleVerifier(EvidenceStore store) => _store = store;
    public string Id => "rule";
    public VerifierLevel Level => VerifierLevel.L2_Rule;

    public Task VerifyAsync(DecisionCase c, CancellationToken ct = default)
    {
        foreach (var claim in c.Claims)
        {
            var unknown = _store.UnknownCitations(claim);
            if (unknown.Count > 0)
            {
                c.Verifications.Add(new VerificationResult(Id, Level, claim.Id, false, 0, $"引用不存在的證據：{string.Join(",", unknown)}"));
                claim.IsOpinion = true; continue;
            }
            if (claim.EvidenceFor.Count == 0)
            {
                c.Verifications.Add(new VerificationResult(Id, Level, claim.Id, false, 0, "沒有任何支持證據 → 標記為意見，不進機率引擎"));
                claim.IsOpinion = true; continue;
            }
            double forW     = claim.EvidenceFor.Sum(id => _store.Get(id)!.Reliability);
            double againstW = claim.EvidenceAgainst.Sum(id => _store.Get(id)!.Reliability);
            double support  = forW / (forW + againstW);
            c.Verifications.Add(new VerificationResult(Id, Level, claim.Id, support >= 0.5, support,
                $"支持 {claim.EvidenceFor.Count} 條 / 反對 {claim.EvidenceAgainst.Count} 條，可靠度加權支持度 {support:F2}"));
        }
        return Task.CompletedTask;
    }
}

/// <summary>L1：LLM 批判。Goodhart 防護：批判者不得是本案任何 solver；分數只當初篩。</summary>
public sealed class LlmCriticVerifier : IVerifier
{
    private readonly AgentRegistry _registry;
    private readonly AgentRunner _runner;
    public LlmCriticVerifier(AgentRegistry registry, AgentRunner runner) { _registry = registry; _runner = runner; }
    public string Id => "llm-critic";
    public VerifierLevel Level => VerifierLevel.L1_LlmCritic;

    public async Task VerifyAsync(DecisionCase c, CancellationToken ct = default)
    {
        string domain = c.Profile?.Domain ?? c.Request.Domain;
        var critic = _registry.Select("critic", domain, 1, exclude: c.AgentsUsedAs("solver")).FirstOrDefault()
                     ?? throw new InvalidOperationException("找不到獨立的 critic（所有候選都是本案 solver）");

        string user = $"問題：{c.Request.Problem}\n\n證據：\n{EvidenceStore.RenderForPrompt(c.Evidence)}\n\n主張：\n" +
                      string.Join("\n", c.Claims.Select(k => $"{k.Id}: {k.Statement}（支持 {string.Join(",", k.EvidenceFor)}；反對 {string.Join(",", k.EvidenceAgainst)}）"));
        var run = await _runner.RunAsync(critic, "critic", RolePrompts.Critic, user, c, 0.2, ct);
        if (!run.Ok) throw new InvalidOperationException("critic 執行失敗：" + run.Error);

        foreach (var cr in AgentOutputs.Critiques(run.RawOutput))
            c.Verifications.Add(new VerificationResult($"{Id}:{critic.AgentId}", Level, cr.ClaimId, cr.Severity < 0.5, 1 - cr.Severity, cr.Issue));
    }
}

/// <summary>L3：可執行測試（編譯 / 單元測試 / property test / 模擬器）。test 回傳 null 表示不適用該主張。</summary>
public sealed class ExecutableTestVerifier : IVerifier
{
    private readonly Func<Claim, DecisionCase, (bool Pass, double Score, string Note)?> _test;
    public ExecutableTestVerifier(string id, Func<Claim, DecisionCase, (bool, double, string)?> test) { Id = id; _test = test; }
    public string Id { get; }
    public VerifierLevel Level => VerifierLevel.L3_ExecutableTest;

    public Task VerifyAsync(DecisionCase c, CancellationToken ct = default)
    {
        foreach (var claim in c.Claims)
        {
            var r = _test(claim, c);
            if (r is null) continue;
            c.Verifications.Add(new VerificationResult(Id, Level, claim.Id, r.Value.Pass, r.Value.Score, r.Value.Note));
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// L4：真實實驗。只跑「已預先登記似然表」的實驗；結果寫成 ExperimentResult 證據，供 Bayesian 更新。
/// run 的正式實作是 HIL / staging / A/B；這裡由呼叫端注入。
/// </summary>
public sealed class ExperimentVerifier : IVerifier
{
    private readonly EvidenceStore _store;
    private readonly Func<Experiment, DecisionCase, CancellationToken, Task<int>> _run;   // 回傳觀察到的 outcome index
    public ExperimentVerifier(EvidenceStore store, Func<Experiment, DecisionCase, CancellationToken, Task<int>> run) { _store = store; _run = run; }
    public string Id => "experiment";
    public VerifierLevel Level => VerifierLevel.L4_Experiment;

    public async Task VerifyAsync(DecisionCase c, CancellationToken ct = default)
    {
        foreach (var exp in c.Experiments.Where(e => e.ObservedOutcome is null))
        {
            if (exp.LikelihoodByClaim.Count == 0) { c.Add($"[{exp.Id}] 沒有預先登記的似然表 → 不跑（防事後解釋）"); continue; }
            int observed = await _run(exp, c, ct);
            exp.ObservedOutcome = observed;
            var ev = _store.Add(EvidenceSource.ExperimentResult, exp.Id, $"{exp.Description} → 觀察到：{exp.Outcomes[observed]}");
            c.Evidence.Add(ev);
            c.Verifications.Add(new VerificationResult(Id, Level, null, true, 1.0, $"{exp.Id} 觀察到 outcome[{observed}]「{exp.Outcomes[observed]}」→ {ev.Id}"));
        }
    }
}
