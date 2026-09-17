// ============================================================================
//  似然定案（Rev2 §4.8）——確定性區，不得出現 ILlm。
//  designer 只投定性等級；數字由這裡查表定案，並算出 hash 供 L4 執行前比對。
//  來源優先序：歷史頻率 > 定性 elicitation（歷史頻率在 Phase 2 接 ExperimentCatalog）。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Probability;

public interface ILikelihoodElicitor
{
    /// <summary>把提名轉成已登記的實驗。失敗時回傳 null 並附理由。</summary>
    (Experiment? Experiment, string? SkipReason) Register(ExperimentDraft draft, string experimentId,
                                                          IReadOnlyCollection<string> knownClaimIds);
}

public sealed class LikertLikelihoodElicitor : ILikelihoodElicitor
{
    public string RegistrarId { get; init; } = "likelihood-elicitor";

    public (Experiment?, string?) Register(ExperimentDraft draft, string experimentId, IReadOnlyCollection<string> knownClaimIds)
    {
        if (draft.Outcomes.Length < 2)
            return (null, "結果欄少於 2 個 → 這個實驗無法區分任何假設");

        // 只收對「本案確實存在的主張」投的票；對不存在的主張投票一律丟棄
        var votes = draft.Votes.Where(v => knownClaimIds.Contains(v.ClaimLocalId)).ToList();
        if (votes.Count == 0)
            return (null, "沒有任何對現存主張的似然投票 → 不登記（防事後解釋）");

        var likert = ImmutableDictionary.CreateBuilder<string, ImmutableArray<LikertBelief>>();
        var probs  = ImmutableDictionary.CreateBuilder<string, ImmutableArray<double>>();

        foreach (var v in votes.OrderBy(v => v.ClaimLocalId, StringComparer.Ordinal))
        {
            // 每個結果欄一個等級；缺的補 EvenOdds（無資訊），多的截斷
            var byOutcome = Enumerable.Range(0, draft.Outcomes.Length)
                .Select(i => i < v.ByOutcome.Length ? v.ByOutcome[i] : LikertBelief.EvenOdds)
                .ToImmutableArray();
            likert[v.ClaimLocalId] = byOutcome;
            probs[v.ClaimLocalId]  = byOutcome.Select(Likert.ToProbability).ToImmutableArray();
        }

        var pr = probs.ToImmutable();
        string hash = Experiment.ComputeHash(draft.Description, draft.Outcomes, pr);

        return (new Experiment(experimentId, draft.Description, draft.Outcomes, pr, likert.ToImmutable(),
                               draft.NominatedBy, RegistrarId, hash, "likert_median", null), null);
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
