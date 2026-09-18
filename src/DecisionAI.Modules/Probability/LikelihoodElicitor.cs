// ============================================================================
//  似然定案（Rev2 §4.8）——確定性區，不得出現 ILlm。
//  designer 只投定性等級；數字由這裡定案，並算出 hash 供 L4 執行前比對。
//  來源優先序：歷史頻率 > 定性 elicitation。
//  歷史頻率是最硬的來源——它不是估出來的，是在已知真假設的條件下數出來的。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Modules.Catalog;

namespace DecisionAI.Modules.Probability;

/// <summary>登記時需要的上下文：哪些主張存在、各自的 catalog id、以及實驗的歷史。</summary>
public sealed record ElicitationContext(
    ImmutableDictionary<string, string> CatalogIdByLocalId,
    ExperimentCatalogView History)
{
    public static ElicitationContext None => new(ImmutableDictionary<string, string>.Empty,
                                                 new ExperimentCatalogView(0, ImmutableArray<HistoricalLikelihood>.Empty));
    public IReadOnlyCollection<string> KnownClaimIds => CatalogIdByLocalId.Keys.ToList();
}

public interface ILikelihoodElicitor
{
    (Experiment? Experiment, string? SkipReason) Register(ExperimentDraft draft, string experimentId, ElicitationContext ctx);
}

public sealed class LikertLikelihoodElicitor : ILikelihoodElicitor
{
    public string RegistrarId { get; init; } = "likelihood-elicitor";

    /// <summary>歷史累積到這個觀察數才夠格取代定性估計。</summary>
    public int MinHistory { get; init; } = 5;

    public (Experiment?, string?) Register(ExperimentDraft draft, string experimentId, ElicitationContext ctx)
    {
        if (draft.Outcomes.Length < 2)
            return (null, "結果欄少於 2 個 → 這個實驗無法區分任何假設");

        var known = ctx.KnownClaimIds;
        var votes = draft.Votes.Where(v => known.Contains(v.ClaimLocalId)).ToList();
        if (votes.Count == 0)
            return (null, "沒有任何對現存主張的似然投票 → 不登記（防事後解釋）");

        string expKey = InMemoryExperimentCatalog.KeyOf(draft.Description);
        var history = ctx.History.For(expKey).ToDictionary(h => h.ClaimCatalogId);

        var likert = ImmutableDictionary.CreateBuilder<string, ImmutableArray<LikertBelief>>();
        var probs = ImmutableDictionary.CreateBuilder<string, ImmutableArray<double>>();
        int fromHistory = 0;

        foreach (var v in votes.OrderBy(v => v.ClaimLocalId, StringComparer.Ordinal))
        {
            var byOutcome = Enumerable.Range(0, draft.Outcomes.Length)
                .Select(i => i < v.ByOutcome.Length ? v.ByOutcome[i] : LikertBelief.EvenOdds)
                .ToImmutableArray();
            likert[v.ClaimLocalId] = byOutcome;

            string catalogId = ctx.CatalogIdByLocalId.GetValueOrDefault(v.ClaimLocalId, "");
            if (catalogId.Length > 0 && history.TryGetValue(catalogId, out var h)
                && h.Total >= MinHistory && h.OutcomeCounts.Length == draft.Outcomes.Length)
            {
                probs[v.ClaimLocalId] = h.Frequencies;      // 歷史頻率優先
                fromHistory++;
            }
            else
            {
                probs[v.ClaimLocalId] = byOutcome.Select(Likert.ToProbability).ToImmutableArray();
            }
        }

        var pr = probs.ToImmutable();
        string hash = Experiment.ComputeHash(draft.Description, draft.Outcomes, pr);
        string source = fromHistory == 0 ? "likert_median"
                      : fromHistory == votes.Count ? "catalog_frequency"
                      : $"mixed({fromHistory}/{votes.Count} 來自歷史)";

        return (new Experiment(experimentId, draft.Description, draft.Outcomes, pr, likert.ToImmutable(),
                               draft.NominatedBy, RegistrarId, hash, source, null), null);
    }
}

/// <summary>多個 designer 對同一實驗投票時，逐格取中位數（抗離群優於平均）。</summary>
public static class LikertAggregation
{
    public static ImmutableArray<LikertVote> Merge(IEnumerable<ImmutableArray<LikertVote>> ballots, int outcomeCount)
    {
        var byClaim = new Dictionary<string, List<LikertBelief>[]>();
        foreach (var ballot in ballots)
            foreach (var v in ballot)
            {
                if (!byClaim.TryGetValue(v.ClaimLocalId, out var cells))
                    byClaim[v.ClaimLocalId] = cells = Enumerable.Range(0, outcomeCount).Select(_ => new List<LikertBelief>()).ToArray();
                for (int i = 0; i < outcomeCount && i < v.ByOutcome.Length; i++) cells[i].Add(v.ByOutcome[i]);
            }

        return byClaim.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(kv => new LikertVote(kv.Key, kv.Value.Select(Likert.Median).ToImmutableArray()))
            .ToImmutableArray();
    }
}
