// ============================================================================
//  EVOI 實驗選擇（Rev2 §4.9）——確定性區，不得出現 ILlm。
//
//  為什麼用 EVOI 而不是資訊增益：資訊多不等於值得做。
//  若實驗無論出哪個結果、推薦行動都一樣，這個實驗就不該做——它只是讓人心安。
//  給定信念分布與似然表，EVOI 是唯一決定的，因此實驗選擇不再隨 LLM 抽樣而變。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Decision;

public sealed record ExperimentValuation(
    string DraftId, double Evoi, double CurrentBest, double ExpectedAfter, double Cost, string Detail);

public sealed record ExperimentChoice(
    ImmutableArray<ExperimentValuation> Run,
    ImmutableArray<ExperimentValuation> Skip);

public interface IExperimentSelector
{
    ExperimentChoice Select(IReadOnlyList<(string DraftId, ImmutableArray<string> Outcomes,
                                           ImmutableDictionary<string, ImmutableArray<double>> Likelihoods, double Cost)> nominations,
                            IReadOnlyDictionary<string, double> beliefs,
                            ImmutableArray<ActionOption> utilities);
}

public sealed class EvoiExperimentSelector : IExperimentSelector
{
    /// <summary>沒有效用矩陣時的退路：把「期望熵下降」換算成效用的係數。</summary>
    public double ValuePerBit { get; init; } = 100;

    /// <summary>EVOI 要超過這個門檻才值得做（預設 0：剛好打平就不做）。</summary>
    public double Threshold { get; init; } = 0;

    public ExperimentChoice Select(
        IReadOnlyList<(string DraftId, ImmutableArray<string> Outcomes,
                       ImmutableDictionary<string, ImmutableArray<double>> Likelihoods, double Cost)> nominations,
        IReadOnlyDictionary<string, double> beliefs,
        ImmutableArray<ActionOption> utilities)
    {
        var run = ImmutableArray.CreateBuilder<ExperimentValuation>();
        var skip = ImmutableArray.CreateBuilder<ExperimentValuation>();

        foreach (var n in nominations.OrderBy(n => n.DraftId, StringComparer.Ordinal))
        {
            var v = utilities.Length > 0
                ? ValueWithUtilities(n, beliefs, utilities)
                : ValueWithInformation(n, beliefs);
            (v.Evoi > Threshold ? run : skip).Add(v);
        }

        return new ExperimentChoice(run.ToImmutable(), skip.ToImmutable());
    }

    /// <summary>EVOI = E[做完實驗後的最佳決策效用] − 當前最佳決策效用 − 實驗成本。</summary>
    private static ExperimentValuation ValueWithUtilities(
        (string DraftId, ImmutableArray<string> Outcomes, ImmutableDictionary<string, ImmutableArray<double>> Likelihoods, double Cost) n,
        IReadOnlyDictionary<string, double> beliefs, ImmutableArray<ActionOption> utilities)
    {
        double BestEu(IReadOnlyDictionary<string, double> p)
            => utilities.Max(a => p.Sum(kv => kv.Value * a.Utility(kv.Key)));

        double now = BestEu(beliefs);
        double after = 0;
        var perOutcome = new List<string>();

        for (int o = 0; o < n.Outcomes.Length; o++)
        {
            // P(outcome) = Σ_h P(h)·L(h,o)；沒登記似然的假設以 0.5 參與（無資訊）
            var joint = beliefs.ToDictionary(kv => kv.Key,
                kv => kv.Value * Lik(n.Likelihoods, kv.Key, o));
            double pOutcome = joint.Values.Sum();
            if (pOutcome <= 1e-12) { perOutcome.Add($"outcome[{o}] P≈0"); continue; }

            var posterior = joint.ToDictionary(kv => kv.Key, kv => kv.Value / pOutcome);
            double eu = BestEu(posterior);
            after += pOutcome * eu;
            string bestAction = utilities.OrderByDescending(a => posterior.Sum(kv => kv.Value * a.Utility(kv.Key)))
                                         .ThenBy(a => a.Id, StringComparer.Ordinal).First().Id;
            perOutcome.Add($"outcome[{o}] P={pOutcome:F2} → 最佳行動 {bestAction}（EU {eu:F1}）");
        }

        double evoi = after - now - n.Cost;
        bool sameAction = perOutcome.Select(x => x.Contains("最佳行動") ? x.Split("最佳行動 ")[1].Split('（')[0] : "")
                                    .Where(x => x.Length > 0).Distinct().Count() <= 1;
        string why = sameAction && evoi <= 0
            ? $"無論出哪個結果推薦行動都一樣 → 這個實驗只是讓人心安（{string.Join("；", perOutcome)}）"
            : string.Join("；", perOutcome);

        return new ExperimentValuation(n.DraftId, Math.Round(evoi, 4), Math.Round(now, 3), Math.Round(after, 3), n.Cost,
            $"EVOI = {after:F1} − {now:F1} − 成本 {n.Cost:F2} = {evoi:F2}；{why}");
    }

    /// <summary>沒有效用矩陣時的退路：期望熵下降 × ValuePerBit − 成本。這是次佳準則，會明示在事件流裡。</summary>
    private ExperimentValuation ValueWithInformation(
        (string DraftId, ImmutableArray<string> Outcomes, ImmutableDictionary<string, ImmutableArray<double>> Likelihoods, double Cost) n,
        IReadOnlyDictionary<string, double> beliefs)
    {
        double H(IReadOnlyDictionary<string, double> p)
            => p.Count < 2 ? 0 : -p.Values.Where(x => x > 0).Sum(x => x * Math.Log(x, 2));

        double now = H(beliefs), expected = 0;
        for (int o = 0; o < n.Outcomes.Length; o++)
        {
            var joint = beliefs.ToDictionary(kv => kv.Key, kv => kv.Value * Lik(n.Likelihoods, kv.Key, o));
            double pOutcome = joint.Values.Sum();
            if (pOutcome <= 1e-12) continue;
            expected += pOutcome * H(joint.ToDictionary(kv => kv.Key, kv => kv.Value / pOutcome));
        }

        double bits = now - expected;
        double evoi = bits * ValuePerBit - n.Cost;
        return new ExperimentValuation(n.DraftId, Math.Round(evoi, 4), Math.Round(now, 3), Math.Round(expected, 3), n.Cost,
            $"沒有效用矩陣 → 退回資訊準則：期望熵下降 {bits:F3} bit × {ValuePerBit} − 成本 {n.Cost:F2} = {evoi:F2}");
    }

    private static double Lik(ImmutableDictionary<string, ImmutableArray<double>> table, string claim, int outcome)
        => table.TryGetValue(claim, out var row) && outcome < row.Length ? row[outcome] : 0.5;
}

/// <summary>Mutation switch：不算 EVOI，提名什麼就跑什麼。</summary>
public sealed class AlwaysRunSelector : IExperimentSelector
{
    public ExperimentChoice Select(
        IReadOnlyList<(string DraftId, ImmutableArray<string> Outcomes,
                       ImmutableDictionary<string, ImmutableArray<double>> Likelihoods, double Cost)> nominations,
        IReadOnlyDictionary<string, double> beliefs, ImmutableArray<ActionOption> utilities)
        => new(nominations.Select(n => new ExperimentValuation(n.DraftId, 1, 0, 0, n.Cost, "（壞的守門件）一律執行")).ToImmutableArray(),
               ImmutableArray<ExperimentValuation>.Empty);
}
