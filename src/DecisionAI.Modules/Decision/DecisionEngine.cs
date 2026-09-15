// ============================================================================
//  Decision / Risk Engine — 「機率知道了，那到底該做什麼？」（確定性區）
//  效用矩陣只能由人（UtilityOwner）寫入 Journal；此資料夾不得出現 ILlm。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Decision;

public interface IDecisionEngine
{
    DecisionResult Decide(IReadOnlyDictionary<string, double> scenarioProbs, ImmutableArray<ActionOption> actions,
                          RiskPolicy policy, IRandomSource rng);
}

public sealed class DecisionEngine : IDecisionEngine
{
    public DecisionResult Decide(IReadOnlyDictionary<string, double> scenarioProbs, ImmutableArray<ActionOption> actions,
                                 RiskPolicy policy, IRandomSource rng)
    {
        var scenarios = scenarioProbs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var metrics = ImmutableDictionary.CreateBuilder<string, ActionMetrics>();
        foreach (var a in actions)
        {
            double eu = scenarios.Sum(s => scenarioProbs[s] * a.Utility(s));
            double worst = scenarios.Min(s => a.Utility(s));
            double regret = scenarios.Max(s => actions.Max(b => b.Utility(s)) - a.Utility(s));
            metrics[a.Id] = new ActionMetrics(eu, worst, regret, Admissible: worst >= -policy.MaxAcceptableLoss);
        }
        var m = metrics.ToImmutable();

        var admissible = actions.Where(a => m[a.Id].Admissible).ToList();
        if (admissible.Count == 0)
            return new DecisionResult(null, 0, 0, 0, 0, $"所有行動的最壞情況都超過可接受損失 {policy.MaxAcceptableLoss}，交人決定", m);

        // 主準則 EU；EU 差距在 5% 內 → 以最大後悔較小者勝出
        var ranked = admissible.OrderByDescending(a => m[a.Id].ExpectedUtility).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var pick = ranked[0];
        if (ranked.Count > 1)
        {
            var second = ranked[1];
            double gap = Math.Abs(m[pick.Id].ExpectedUtility - m[second.Id].ExpectedUtility) / Math.Max(1e-9, Math.Abs(m[pick.Id].ExpectedUtility));
            if (gap < 0.05 && m[second.Id].MaxRegret < m[pick.Id].MaxRegret) pick = second;
        }

        double robustness = Robustness(scenarioProbs, scenarios, admissible, pick, policy, rng.Fork("robustness"));
        var pm = m[pick.Id];
        var rival = ranked.FirstOrDefault(a => a.Id != pick.Id);
        string reason = rival is null
            ? "唯一可接受的行動。"
            : $"{pick.Id} EU={pm.ExpectedUtility:F1}，{rival.Id} EU={m[rival.Id].ExpectedUtility:F1}；" +
              $"{pick.Id} 最壞情況 {pm.WorstCase:F0}、最大後悔 {pm.MaxRegret:F0}；機率擾動 ±{policy.RobustnessDelta:P0} 下 {robustness:P0} 的情況選擇不變。";

        return new DecisionResult(pick.Id, pm.ExpectedUtility, pm.WorstCase, pm.MaxRegret, robustness, reason, m);
    }

    /// <summary>穩健性：把每個情境機率隨機擾動 ±δ、正規化，看 argmax EU 有多少比例仍是同一個行動。</summary>
    private static double Robustness(IReadOnlyDictionary<string, double> probs, List<string> keys, List<ActionOption> actions,
                                     ActionOption pick, RiskPolicy policy, IRandomSource rng)
    {
        int same = 0;
        for (int i = 0; i < policy.RobustnessSamples; i++)
        {
            var p = keys.ToDictionary(k => k, k => Math.Max(0.001, probs[k] + (rng.NextDouble() * 2 - 1) * policy.RobustnessDelta));
            double z = p.Values.Sum();
            var best = actions.MaxBy(a => keys.Sum(k => p[k] / z * a.Utility(k)))!;
            if (best.Id == pick.Id) same++;
        }
        return same / (double)policy.RobustnessSamples;
    }
}
