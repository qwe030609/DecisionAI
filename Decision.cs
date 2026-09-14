// ============================================================================
//  Decision / Risk Engine — 「機率知道了，那到底該做什麼？」（確定性區）
//  效用矩陣 U(a, s) 是人或政策表給的；機率 P(s) 是 Probability Engine 給的；LLM 不碰數字。
//  沒有效用矩陣（價值觀問題、反身系統）→ 不算 EU，把分布與備忘交給人。
// ============================================================================

using DecisionAI.Domain;

namespace DecisionAI.Decision;

public sealed class DecisionEngine
{
    public DecisionResult Decide(IReadOnlyDictionary<string, double> scenarioProbs, IReadOnlyList<ActionOption> actions,
                                 RiskPolicy policy, VerifierLevel bestPassedLevel)
    {
        var scenarios = scenarioProbs.Keys.ToList();
        double U(ActionOption a, string s) => a.UtilityByScenario.GetValueOrDefault(s, 0);

        var metrics = new Dictionary<string, ActionMetrics>();
        foreach (var a in actions)
        {
            double eu    = scenarios.Sum(s => scenarioProbs[s] * U(a, s));
            double worst = scenarios.Min(s => U(a, s));
            double regret = scenarios.Max(s => actions.Max(b => U(b, s)) - U(a, s));
            metrics[a.Id] = new ActionMetrics(eu, worst, regret, Admissible: worst >= -policy.MaxAcceptableLoss);   // 失敗成本上限
        }

        var admissible = actions.Where(a => metrics[a.Id].Admissible).ToList();
        if (admissible.Count == 0)
            return new DecisionResult(null, 0, 0, 0, 0, 0, $"所有行動的最壞情況都超過可接受損失 {policy.MaxAcceptableLoss}，交人決定", metrics);

        // 主準則 EU；EU 差距在 5% 內 → 以最大後悔較小者勝出
        var ranked = admissible.OrderByDescending(a => metrics[a.Id].ExpectedUtility).ToList();
        var pick = ranked[0];
        if (ranked.Count > 1)
        {
            var second = ranked[1];
            double gap = Math.Abs(metrics[pick.Id].ExpectedUtility - metrics[second.Id].ExpectedUtility)
                       / Math.Max(1e-9, Math.Abs(metrics[pick.Id].ExpectedUtility));
            if (gap < 0.05 && metrics[second.Id].MaxRegret < metrics[pick.Id].MaxRegret) pick = second;
        }

        double robustness = Robustness(scenarioProbs, admissible, pick, policy);
        double confidence = VerifierTrust.Cap(bestPassedLevel) * robustness;   // 上限來自驗證等級，不來自 LLM

        var m = metrics[pick.Id];
        var rival = ranked.FirstOrDefault(a => a.Id != pick.Id);
        string reason = rival is null
            ? $"唯一可接受的行動。"
            : $"{pick.Id} EU={m.ExpectedUtility:F1}，{rival.Id} EU={metrics[rival.Id].ExpectedUtility:F1}；" +
              $"{pick.Id} 最壞情況 {m.WorstCase:F0}、最大後悔 {m.MaxRegret:F0}；機率擾動 ±{policy.RobustnessDelta:P0} 下 {robustness:P0} 的情況選擇不變；" +
              $"信心上限來自驗證等級 {bestPassedLevel}。";

        return new DecisionResult(pick.Id, m.ExpectedUtility, m.WorstCase, m.MaxRegret, robustness, confidence, reason, metrics);
    }

    /// <summary>穩健性：把每個情境機率隨機擾動 ±δ、正規化，看 argmax EU 有多少比例仍是同一個行動。</summary>
    private static double Robustness(IReadOnlyDictionary<string, double> probs, List<ActionOption> actions, ActionOption pick, RiskPolicy policy)
    {
        var rng = new Random(12345);   // 固定種子：同樣輸入同樣輸出（可審計）
        var keys = probs.Keys.ToList();
        int same = 0;
        for (int i = 0; i < policy.RobustnessSamples; i++)
        {
            var p = keys.ToDictionary(k => k, k => Math.Max(0.001, probs[k] + (rng.NextDouble() * 2 - 1) * policy.RobustnessDelta));
            double z = p.Values.Sum();
            var best = actions.MaxBy(a => keys.Sum(k => p[k] / z * a.UtilityByScenario.GetValueOrDefault(k, 0)))!;
            if (best.Id == pick.Id) same++;
        }
        return same / (double)policy.RobustnessSamples;
    }
}
