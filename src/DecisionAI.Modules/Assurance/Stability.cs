// ============================================================================
//  三擾動穩定層 + Chao1 假設覆蓋率（Rev2 §4.10 / Phase 2）
//  確定性區，不得出現 ILlm。
//
//  關鍵論點：決策穩健不需要假設集完全相同，只需要決策不變。
//  top-2 假設互換但推薦行動一樣，那就是穩健的。
//  第二項擾動把「每次跑不一樣」從隱憂變成一個可稽核的數字；
//  它低的時候系統應該自己說「這題我跑五次會有兩次給不同建議」，而不是假裝穩定。
//  「可稽核」在這裡是硬要求：這個數字用列舉算而不是抽樣算，任何人都能重算出同一個值。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Decision;
using DecisionAI.Modules.Probability;

namespace DecisionAI.Modules.Assurance;

public sealed record ResamplingVerdict(double StabilityRate, int Trials, string BaselineAction, string Detail);

public interface IDecisionStabilityAnalyzer
{
    /// <summary>
    /// 假設集重抽樣：把「如果當時抽到的是另一組 solver 輸出，結論會不會變」變成一個數字。
    ///
    /// 做法刻意是「列舉所有非空的 solver 子集」而不是 bootstrap 抽樣：
    /// bootstrap 在 25 次下的標準誤差高達 ±5～10 個百分點，那個數字本身比它要量的效應還糊，
    /// 而且沒有人能重算它來推翻它。列舉是確定性的、可被獨立重算的，
    /// 也因此是可稽核的——這比「看起來更像統計」重要。
    /// solver 多到列舉太貴時（> MaxEnumerable）才退回抽樣。
    /// </summary>
    ResamplingVerdict Analyze(CaseState s, PolicySnapshot policy, IRandomSource rng, int trials = 25);
}

public sealed class DecisionStabilityAnalyzer : IDecisionStabilityAnalyzer
{
    private readonly IEnsembler _ensembler;
    private readonly IBayesUpdater _bayes;
    private readonly IDecisionEngine _decision;

    public DecisionStabilityAnalyzer(IEnsembler ensembler, IBayesUpdater bayes, IDecisionEngine decision)
    { _ensembler = ensembler; _bayes = bayes; _decision = decision; }

    /// <summary>超過這個 solver 數就改用抽樣（2^n 個子集）。</summary>
    public int MaxEnumerable { get; init; } = 10;

    public ResamplingVerdict Analyze(CaseState s, PolicySnapshot policy, IRandomSource rng, int trials = 25)
    {
        string baseline = s.Decision?.RecommendedAction ?? "";
        if (baseline.Length == 0 || s.Utilities.Length == 0)
            return new ResamplingVerdict(1, 0, baseline, "沒有推薦行動 → 不做重抽樣");

        var proposers = s.AgentsUsedAs("solver").OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (proposers.Count < 2)
            return new ResamplingVerdict(1, 0, baseline, "只有一個 solver → 沒有可重抽的樣本（Assurance 已由降級階梯反映）");

        var subsets = proposers.Count <= MaxEnumerable
            ? Enumerate(proposers)
            : Bootstrap(proposers, trials, rng.Fork("claim_resampling"));

        int same = 0, ran = 0;
        var flips = new Dictionary<string, int>();
        var stream = rng.Fork("resampling_decision");

        foreach (var drawn in subsets)
        {
            var resampled = Resample(s, drawn);
            if (!resampled.Claims.Any(c => c.Kind == ClaimKind.Hypothesis && !c.IsOpinion)) continue;

            var prior = _ensembler.Prior(resampled, policy);
            if (prior.Count == 0) continue;
            foreach (var exp in resampled.Experiments.Where(e => e.ObservedOutcome is not null))
                prior = _bayes.Update(prior, exp);

            var d = _decision.Decide(prior, resampled.AlignedUtilities,
                                     resampled.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity), stream);
            ran++;
            if (d.RecommendedAction == baseline) same++;
            else flips[d.RecommendedAction ?? "（無）"] = flips.GetValueOrDefault(d.RecommendedAction ?? "（無）") + 1;
        }

        double rate = ran == 0 ? 1 : same / (double)ran;
        string how = proposers.Count <= MaxEnumerable ? $"{ran} 個 solver 子集" : $"{ran} 次重抽";
        string detail = ran == 0
            ? "重抽樣後沒有可用的假設 → 視為穩定"
            : $"{how}中 {same} 個仍推薦 {baseline}（{rate:P0}）" +
              (flips.Count > 0 ? $"；改推薦：{string.Join("、", flips.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}"))}" : "");
        return new ResamplingVerdict(rate, ran, baseline, detail);
    }

    /// <summary>所有非空子集，順序固定（可重放且可被獨立重算）。</summary>
    private static IEnumerable<ImmutableHashSet<string>> Enumerate(List<string> proposers)
    {
        for (int mask = 1; mask < (1 << proposers.Count); mask++)
            yield return Enumerable.Range(0, proposers.Count)
                .Where(i => (mask & (1 << i)) != 0)
                .Select(i => proposers[i]).ToImmutableHashSet();
    }

    private static IEnumerable<ImmutableHashSet<string>> Bootstrap(List<string> proposers, int trials, IRandomSource stream)
    {
        for (int t = 0; t < trials; t++)
            yield return Enumerable.Range(0, proposers.Count)
                .Select(_ => proposers[stream.Next(proposers.Count)]).ToImmutableHashSet();
    }

    /// <summary>只保留被抽中的 solver 的提名；主張的證據引用跟著剩下的提案走。</summary>
    private static CaseState Resample(CaseState s, ImmutableHashSet<string> drawnAgents)
    {
        var claims = s.Claims
            .Select(c => c with { Proposals = c.Proposals.Where(p => drawnAgents.Contains(p.AgentId)).ToImmutableList() })
            .Where(c => c.Kind != ClaimKind.Hypothesis || c.Proposals.Count > 0)
            .ToImmutableList();

        var runs = s.AgentRuns.Where(r => r.Role != "solver" || drawnAgents.Contains(r.AgentId)).ToImmutableList();
        return s with { Claims = claims, AgentRuns = runs };
    }
}

/// <summary>Mutation switch：永遠回報完全穩定。</summary>
public sealed class AlwaysStableAnalyzer : IDecisionStabilityAnalyzer
{
    public ResamplingVerdict Analyze(CaseState s, PolicySnapshot policy, IRandomSource rng, int trials = 25)
        => new(1, trials, s.Decision?.RecommendedAction ?? "", "（壞的守門件）一律回報穩定");
}

// ── Chao1 假設覆蓋率 ────────────────────────────────────────────────────

public interface IHypothesisSaturationEstimator
{
    SaturationEstimate Estimate(CaseState s, int agentCount);
}

/// <summary>
/// Chao1：未發現數 ≈ f1² / (2·f2)，f1 = 只有一個 agent 提過的主張數，f2 = 剛好兩個提過的。
/// singletons 多 → 假設空間探索不足 → 能力層降級。
/// 停止準則也隨之從「固定跑 N 個 solver」改成飽和停止。
/// </summary>
public sealed class Chao1SaturationEstimator : IHypothesisSaturationEstimator
{
    public SaturationEstimate Estimate(CaseState s, int agentCount)
    {
        var hyps = s.Claims.Where(c => c.Kind == ClaimKind.Hypothesis).ToList();
        int f1 = hyps.Count(c => c.Proposals.Select(p => p.AgentId).Distinct().Count() == 1);
        int f2 = hyps.Count(c => c.Proposals.Select(p => p.AgentId).Distinct().Count() == 2);
        double undiscovered = f2 > 0 ? f1 * (double)f1 / (2.0 * f2)
                            : f1 * (f1 - 1) / 2.0;                 // f2 = 0 時的偏誤修正式
        return new SaturationEstimate(f1, f2, Math.Round(Math.Max(0, undiscovered), 2));
    }

    /// <summary>覆蓋率：已發現 / (已發現 + 估計未發現)。低於門檻就該再抽一個 solver。</summary>
    public static double Coverage(int discovered, SaturationEstimate e)
        => discovered + e.EstimatedUndiscovered <= 0 ? 1 : discovered / (discovered + e.EstimatedUndiscovered);
}
