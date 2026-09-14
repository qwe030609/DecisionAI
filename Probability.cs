// ============================================================================
//  Probability Engine — 「不是誰贏，而是誰有多大機率正確」（確定性區）
//  LLM 只提供輸入：自報信心、預先登記的似然。校準、加權、相關性折扣、Bayesian 更新全在這裡算。
// ============================================================================

using DecisionAI.Agents;
using DecisionAI.Domain;

namespace DecisionAI.Probability;

/// <summary>校準：記錄每個 agent 的（自報機率, 實際結果），算 Brier / ECE，並把未來的自報機率往回校。</summary>
public sealed class CalibrationEngine
{
    private readonly Dictionary<string, List<(double P, bool Y)>> _records = new();
    private readonly object _lock = new();

    public void Record(string agentId, double p, bool y)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(agentId, out var list)) _records[agentId] = list = new();
            list.Add((Math.Clamp(p, 0.01, 0.99), y));
        }
    }

    public double? Brier(string agentId)
    {
        lock (_lock) return _records.TryGetValue(agentId, out var l) && l.Count > 0 ? l.Average(r => Math.Pow(r.P - (r.Y ? 1 : 0), 2)) : null;
    }

    /// <summary>Expected Calibration Error：分 bins，|平均自報 − 實際命中率| 的加權平均。</summary>
    public double? Ece(string agentId, int bins = 5)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(agentId, out var l) || l.Count == 0) return null;
            double ece = 0;
            foreach (var g in l.GroupBy(r => Math.Min(bins - 1, (int)(r.P * bins))))
                ece += g.Count() / (double)l.Count * Math.Abs(g.Average(r => r.P) - g.Average(r => r.Y ? 1 : 0));
            return ece;
        }
    }

    /// <summary>
    /// 回校：把自報機率往 0.5 收縮。樣本 &lt; 5 → 一律打七折（沒證明自己之前不信）；
    /// 之後依「自報 − 實際」的過度自信程度決定收縮係數（Platt scaling 的一維簡化版）。
    /// </summary>
    public double Recalibrate(string agentId, double p)
    {
        double k;
        lock (_lock)
        {
            if (!_records.TryGetValue(agentId, out var l) || l.Count < 5) k = 0.7;
            else
            {
                double over = l.Average(r => (r.P - 0.5) * ((r.P - 0.5) - ((r.Y ? 1 : 0) - 0.5)) / 0.25); // >0 = 往極端說得比實際多
                k = Math.Clamp(1 - over, 0.3, 1.0);
            }
        }
        return 0.5 + (p - 0.5) * k;
    }

    public string Report()
    {
        lock (_lock)
            return string.Join("\n", _records.OrderBy(k => k.Key)
                .Select(kv => $"{kv.Key,-16} n={kv.Value.Count,-3} Brier={Brier(kv.Key):F3} ECE={Ece(kv.Key):F3}"));
    }
}

/// <summary>集成：對每個假設，把各 agent 的（回校後）機率做加權 log-odds 平均，權重 = 歷史表現，並依錯誤相關性打折。</summary>
public sealed class EnsembleEngine
{
    private readonly AgentRegistry _registry;
    private readonly CalibrationEngine _calibration;
    public EnsembleEngine(AgentRegistry registry, CalibrationEngine calibration) { _registry = registry; _calibration = calibration; }

    public void SetPrior(DecisionCase c, string domain, string role, IReadOnlyList<string> allProposers)
    {
        var hyps = c.Hypotheses.ToList();
        if (hyps.Count == 0) return;

        var raw = new Dictionary<string, double>();
        foreach (var h in hyps)
        {
            double num = 0, den = 0;
            foreach (var agent in allProposers)
            {
                var prop = h.Proposals.FirstOrDefault(p => p.AgentId == agent);
                // 沒提這個假設的人 = 弱的反對票（p=0.15，權重減半）；提了的人用回校後的機率
                double p = prop is null ? 0.15 : _calibration.Recalibrate(agent, prop.StatedConfidence);
                double w = _registry.Stats(agent, domain, role).Mean * (prop is null ? 0.5 : 1.0);
                double corr = allProposers.Where(o => o != agent).Select(o => Math.Max(0, _registry.ErrorCorrelation(agent, o))).DefaultIfEmpty(0).Sum();
                w /= 1 + corr;                                                        // 錯誤相關 → 邊際資訊低
                num += w * Math.Log(p / (1 - p)); den += w;
            }
            double logit = den == 0 ? 0 : num / den;
            raw[h.Id] = 1 / (1 + Math.Exp(-logit));
        }

        // 互斥假設 → 正規化成分布
        double z = raw.Values.Sum();
        c.Beliefs.Clear();
        foreach (var kv in raw) c.Beliefs[kv.Key] = kv.Value / z;
        c.Add("[prob] 先驗（集成）：" + Render(c.Beliefs));
    }

    public static string Render(IReadOnlyDictionary<string, double> b)
        => string.Join("  ", b.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:P0}"));
}

/// <summary>Bayesian 更新：P(H_i | E) ∝ P(H_i) · P(E | H_i)^τ。似然只取自預先登記的表；τ 是回火係數，因為似然是 LLM 給的。</summary>
public sealed class BayesianEngine
{
    public double Temper { get; init; } = 0.7;

    public void Update(DecisionCase c, Experiment exp)
    {
        if (exp.ObservedOutcome is not int obs) return;
        var post = new Dictionary<string, double>();
        foreach (var (h, prior) in c.Beliefs)
        {
            double lik = exp.LikelihoodByClaim.TryGetValue(h, out var row) && obs < row.Length ? row[obs] : 0.5; // 沒登記 = 無資訊
            post[h] = prior * Math.Pow(lik, Temper);
        }
        double z = post.Values.Sum();
        foreach (var kv in post) c.Beliefs[kv.Key] = kv.Value / z;
        c.Add($"[prob] {exp.Id} 觀察 outcome[{obs}] → 後驗：{EnsembleEngine.Render(c.Beliefs)}");
    }

    public static double Entropy(IReadOnlyDictionary<string, double> b)
        => -b.Values.Where(p => p > 0).Sum(p => p * Math.Log(p)) / Math.Log(Math.Max(2, b.Count));
}
