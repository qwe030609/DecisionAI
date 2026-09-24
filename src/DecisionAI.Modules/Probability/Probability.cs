// ============================================================================
//  Probability（Rev2 Phase 2，確定性區，不得出現 ILlm）
//    ICalibrator   自報信心 → 依歷史表現回校
//    IEnsembler    加權 log-odds 集成，權重依錯誤相關性折扣
//    IBayesUpdater 回火 Bayesian 更新，似然只取自預先登記的表
//    ILikelihoodSensitivityAnalyzer  似然等級 ±1 → 後驗排序會不會變
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;

namespace DecisionAI.Modules.Probability;

// ── 校準 ────────────────────────────────────────────────────────────────

public interface ICalibrator
{
    double Recalibrate(string agentId, double p, PolicySnapshot policy);
}

/// <summary>
/// 兩段式回校：
///   · 這個自報機率「那一桶」有足夠樣本 → 往該桶的實際命中率拉（Platt / isotonic 的樸素版）
///   · 樣本不足 → 退回整體收縮係數，往 0.5 收
///
/// 為什麼要分桶：全域的過度自信程度會把不同機率值的誤差平均掉。
/// 一個每次都喊「幾乎確定」但只有六成對的 agent，如果他在低信心那一帶還算準，
/// 全域平均就會顯示他「只是稍微樂觀」——然後他繼續用 0.9 主導後驗。
/// 真正要修的是 0.9 那一桶，而要修它就得看那一桶自己的資料。
/// </summary>
public sealed class CalibrationEngine : ICalibrator
{
    /// <summary>桶內樣本達到這個數才採用桶內命中率；之前只能部分採用。</summary>
    public int FullTrustAt { get; init; } = 20;

    /// <summary>桶寬（±）。太窄會沒有樣本，太寬就退化成全域平均。</summary>
    public double HalfWidth { get; init; } = 0.1;

    public double Recalibrate(string agentId, double p, PolicySnapshot policy)
    {
        var curve = policy.Curve(agentId);
        if (curve.Bucket(p, HalfWidth) is { } bucket && bucket.N >= 5)
        {
            // 桶內樣本越多，越相信桶內的實際命中率；樣本少時仍以自報值為主
            double trust = Math.Min(1.0, bucket.N / (double)FullTrustAt);
            return Math.Clamp(p + (bucket.Rate - p) * trust, 0.01, 0.99);
        }

        double k = curve.ShrinkFactor;
        return Math.Clamp(0.5 + (p - 0.5) * k, 0.01, 0.99);
    }
}

/// <summary>Mutation switch：不回校 → 過度自信的 agent 直接主導後驗。</summary>
public sealed class NoOpCalibrator : ICalibrator
{
    public double Recalibrate(string agentId, double p, PolicySnapshot policy) => Math.Clamp(p, 0.01, 0.99);
}

// ── 集成 ────────────────────────────────────────────────────────────────

public interface IEnsembler
{
    ImmutableDictionary<string, double> Prior(CaseState s, PolicySnapshot policy);
    /// <summary>集成時實際採用的權重與折扣，寫進事件流供稽核。</summary>
    ImmutableArray<string> LastRationale { get; }
}

/// <summary>
/// 加權 log-odds 平均。權重 = 歷史表現（Beta 後驗均值），再除以 (1 + 與其他人的相關性總和)：
/// 錯誤相關 → 邊際資訊低 → 權重降低。沒提這個假設的人算弱的反對票。
/// </summary>
public sealed class CalibratedEnsembler : IEnsembler
{
    private readonly ICalibrator _calibrator;
    private readonly ICorrelationEstimator _correlation;
    private ImmutableArray<string> _rationale = ImmutableArray<string>.Empty;

    public CalibratedEnsembler(ICalibrator? calibrator = null, ICorrelationEstimator? correlation = null)
    { _calibrator = calibrator ?? new CalibrationEngine(); _correlation = correlation ?? new BlendedCorrelation(); }

    public ImmutableArray<string> LastRationale => _rationale;

    /// <summary>沒提出某假設的人視為弱反對票的機率。</summary>
    public double AbstainVote { get; init; } = 0.15;

    public ImmutableDictionary<string, double> Prior(CaseState s, PolicySnapshot policy)
    {
        var hyps = s.Hypotheses.ToList();
        if (hyps.Count == 0) { _rationale = ImmutableArray<string>.Empty; return ImmutableDictionary<string, double>.Empty; }

        var proposers = s.AgentsUsedAs("solver").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var ctx = BlendedCorrelation.ContextFrom(s, policy);
        var why = ImmutableArray.CreateBuilder<string>();

        // 每個 agent 的折扣後權重只算一次（與假設無關）
        var weight = new Dictionary<string, double>();
        foreach (var agent in proposers)
        {
            double corr = proposers.Where(o => o != agent).Sum(o => Math.Max(0, _correlation.Estimate(agent, o, ctx).Rho));
            double baseW = policy.Weight(agent, s.Domain, "solver").Mean;
            weight[agent] = baseW / (1 + corr);
            why.Add($"{agent}：權重 {baseW:F2} ÷ (1+{corr:F2}) = {weight[agent]:F3}" +
                    $"；校準收縮 k={policy.Curve(agent).ShrinkFactor:F2}（n={policy.Curve(agent).N}）");
        }

        var raw = new Dictionary<string, double>();
        foreach (var h in hyps)
        {
            double num = 0, den = 0;
            foreach (var agent in proposers)
            {
                var prop = h.Proposals.FirstOrDefault(p => p.AgentId == agent);
                double p = prop is null ? AbstainVote : _calibrator.Recalibrate(agent, prop.Probability, policy);
                p = Math.Clamp(p, 0.01, 0.99);
                double w = weight[agent] * (prop is null ? 0.5 : 1.0);
                num += w * Math.Log(p / (1 - p)); den += w;
            }
            raw[h.LocalId] = 1 / (1 + Math.Exp(-(den == 0 ? 0 : num / den)));
        }

        _rationale = why.ToImmutable();
        double z = raw.Values.Sum();
        return raw.OrderBy(k => k.Key, StringComparer.Ordinal).ToImmutableDictionary(k => k.Key, k => k.Value / z);
    }
}

// ── Bayesian 更新 ───────────────────────────────────────────────────────

public interface IBayesUpdater
{
    ImmutableDictionary<string, double> Update(ImmutableDictionary<string, double> prior, Experiment exp);
}

/// <summary>P(H_i | E) ∝ P(H_i) · P(E | H_i)^τ。似然只取自預先登記的表；τ 是回火係數。</summary>
public sealed class TemperedBayesUpdater : IBayesUpdater
{
    public double Temper { get; init; } = 0.7;

    public ImmutableDictionary<string, double> Update(ImmutableDictionary<string, double> prior, Experiment exp)
        => Update(prior, exp.LikelihoodByClaim, exp.ObservedOutcome);

    public ImmutableDictionary<string, double> Update(
        ImmutableDictionary<string, double> prior,
        ImmutableDictionary<string, ImmutableArray<double>> likelihoods, int? observed)
    {
        if (observed is not int obs) return prior;
        var post = new Dictionary<string, double>();
        foreach (var (h, p) in prior)
        {
            double lik = likelihoods.TryGetValue(h, out var row) && obs < row.Length ? row[obs] : 0.5;   // 沒登記 = 無資訊
            post[h] = p * Math.Pow(lik, Temper);
        }
        double z = post.Values.Sum();
        if (z <= 0) return prior;
        return post.OrderBy(k => k.Key, StringComparer.Ordinal).ToImmutableDictionary(k => k.Key, k => k.Value / z);
    }
}

// ── 似然敏感度 ───────────────────────────────────────────────────────────

public sealed record SensitivityVerdict(bool OrderChanged, string? TopUnderShift, string? TopBaseline, string Detail);

public interface ILikelihoodSensitivityAnalyzer
{
    SensitivityVerdict Analyze(ImmutableDictionary<string, double> prior, Experiment exp);
}

/// <summary>
/// 各似然等級整體 ±1 級，重算後驗，檢查排序是否改變。
/// 這是「怎麼維持穩健性」的正解：不是讓似然變準（做不到），
/// 而是讓結論對似然的依賴程度變成可見的。
/// </summary>
public sealed class LikelihoodSensitivityAnalyzer : ILikelihoodSensitivityAnalyzer
{
    private readonly TemperedBayesUpdater _bayes;
    public LikelihoodSensitivityAnalyzer(TemperedBayesUpdater? bayes = null) => _bayes = bayes ?? new TemperedBayesUpdater();

    public SensitivityVerdict Analyze(ImmutableDictionary<string, double> prior, Experiment exp)
    {
        if (exp.ObservedOutcome is not int obs || prior.Count == 0)
            return new(false, null, null, "沒有觀察結果或沒有信念分布 → 不做敏感度分析");

        string? Top(ImmutableDictionary<string, double> d)
            => d.Count == 0 ? null : d.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).First().Key;

        var baseline = Top(_bayes.Update(prior, exp.LikelihoodByClaim, obs));
        var notes = new List<string>();

        foreach (int delta in new[] { -1, +1 })
        {
            var shifted = exp.LikertByClaim.ToImmutableDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(l => Likert.ToProbability(Likert.Shift(l, delta))).ToImmutableArray());
            if (shifted.Count == 0) continue;

            var top = Top(_bayes.Update(prior, shifted, obs));
            notes.Add($"整體 {delta:+#;-#} 級 → 首位 {top}");
            if (top != baseline)
                return new(true, top, baseline,
                    $"似然等級整體 {delta:+#;-#} 級就會把首位從 {baseline} 換成 {top} → 結論撐在一組估出來的數字上（{string.Join("；", notes)}）");
        }

        return new(false, baseline, baseline, $"似然等級 ±1 級後首位仍是 {baseline}（{string.Join("；", notes)}）");
    }
}

/// <summary>Mutation switch：永遠回報不敏感 → 撐在估計值上的結論看起來很穩。</summary>
public sealed class AlwaysStableSensitivity : ILikelihoodSensitivityAnalyzer
{
    public SensitivityVerdict Analyze(ImmutableDictionary<string, double> prior, Experiment exp)
        => new(false, null, null, "（壞的守門件）一律回報不敏感");
}

public static class Beliefs
{
    public static string Render(IReadOnlyDictionary<string, double> b)
        => string.Join("  ", b.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value:P0}"));

    /// <summary>正規化熵：0 = 完全確定，1 = 完全均勻。</summary>
    public static double Entropy(IReadOnlyDictionary<string, double> b)
        => b.Count < 2 ? 0 : -b.Values.Where(p => p > 0).Sum(p => p * Math.Log(p)) / Math.Log(b.Count);
}
