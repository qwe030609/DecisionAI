// ============================================================================
//  Probability（Phase 1 精簡版，確定性區）。
//  校準、相關性折扣在 Phase 2；這裡只做：固定收縮 + Beta 權重的 log-odds 集成 + 回火 Bayesian 更新。
//  此資料夾不得出現 ILlm。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;

namespace DecisionAI.Modules.Probability;

public interface IEnsembler
{
    ImmutableDictionary<string, double> Prior(CaseState s, PolicySnapshot policy);
}

public sealed class SimpleEnsembler : IEnsembler
{
    /// <summary>樣本 &lt; 5 的 agent 自報信心一律往 0.5 收縮七折（Phase 1 對所有人都是這樣）。</summary>
    public double Shrink { get; init; } = 0.7;

    public ImmutableDictionary<string, double> Prior(CaseState s, PolicySnapshot policy)
    {
        var hyps = s.Hypotheses.ToList();
        if (hyps.Count == 0) return ImmutableDictionary<string, double>.Empty;
        var proposers = s.AgentsUsedAs("solver").ToList();

        var raw = new Dictionary<string, double>();
        foreach (var h in hyps)
        {
            double num = 0, den = 0;
            foreach (var agent in proposers)
            {
                var prop = h.Proposals.FirstOrDefault(p => p.AgentId == agent);
                // 沒提這個假設的人 = 弱的反對票（p=0.15，權重減半）
                double p = prop is null ? 0.15 : 0.5 + (prop.StatedConfidence - 0.5) * Shrink;
                p = Math.Clamp(p, 0.01, 0.99);
                double w = policy.Weight(agent, s.Domain, "solver").Mean * (prop is null ? 0.5 : 1.0);
                num += w * Math.Log(p / (1 - p)); den += w;
            }
            raw[h.Id] = 1 / (1 + Math.Exp(-(den == 0 ? 0 : num / den)));
        }
        double z = raw.Values.Sum();
        return raw.OrderBy(k => k.Key, StringComparer.Ordinal).ToImmutableDictionary(k => k.Key, k => k.Value / z);
    }
}

public interface IBayesUpdater
{
    ImmutableDictionary<string, double> Update(ImmutableDictionary<string, double> prior, Experiment exp);
}

/// <summary>P(H_i | E) ∝ P(H_i) · P(E | H_i)^τ。似然只取自預先登記的表；τ 是回火係數，因為似然是 LLM 給的。</summary>
public sealed class TemperedBayesUpdater : IBayesUpdater
{
    public double Temper { get; init; } = 0.7;

    public ImmutableDictionary<string, double> Update(ImmutableDictionary<string, double> prior, Experiment exp)
    {
        if (exp.ObservedOutcome is not int obs) return prior;
        var post = new Dictionary<string, double>();
        foreach (var (h, p) in prior)
        {
            double lik = exp.LikelihoodByClaim.TryGetValue(h, out var row) && obs < row.Length ? row[obs] : 0.5;   // 沒登記 = 無資訊
            post[h] = p * Math.Pow(lik, Temper);
        }
        double z = post.Values.Sum();
        return post.OrderBy(k => k.Key, StringComparer.Ordinal).ToImmutableDictionary(k => k.Key, k => k.Value / z);
    }
}

public static class Beliefs
{
    public static string Render(IReadOnlyDictionary<string, double> b)
        => string.Join("  ", b.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value:P0}"));
}
