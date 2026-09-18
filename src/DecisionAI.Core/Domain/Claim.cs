// ============================================================================
//  Claim / Experiment — Probabilistic Zone 的產物，型別不可變。
//  Rev2：Claim 帶 ClaimFrame 與 canonical id；Experiment 分成「提名」與「登記」兩段，
//  登記由確定性程式做，並寫入似然 hash（L4 執行前比對）。
// ============================================================================

using System.Collections.Immutable;
using System.Text;

namespace DecisionAI.Core.Domain;

public enum ClaimKind { Hypothesis, Candidate, Forecast }

public sealed record Proposal(string AgentId, LikertBelief Confidence)
{
    public double Probability => Likert.ToProbability(Confidence);
}

/// <summary>Canonicalizer 的判定。Ambiguous 不自動猜：猜錯會把兩個不同故障合併，後驗從此錯到底。</summary>
public enum MatchKind { Exact, Fuzzy, Ambiguous, New }

public sealed record CanonicalizationResult(
    MatchKind Kind, string CatalogId, double MatchScore, ImmutableArray<string> Candidates)
{
    public static CanonicalizationResult Exact(string id) => new(MatchKind.Exact, id, 1.0, ImmutableArray<string>.Empty);
    public static CanonicalizationResult Fuzzy(string id, double score) => new(MatchKind.Fuzzy, id, score, ImmutableArray<string>.Empty);
    public static CanonicalizationResult Ambiguous(ImmutableArray<string> candidates) => new(MatchKind.Ambiguous, "", 0, candidates);
    public static CanonicalizationResult NewEntry(string draftId) => new(MatchKind.New, draftId, 0, ImmutableArray<string>.Empty);
}

public sealed record Claim(
    string LocalId,                       // H1、H2…：依 plan 中 agent 的宣告順序編號
    ClaimKey Key,                         // 跨 case 穩定識別（catalog id 或 DraftId）
    ClaimKind Kind,
    ClaimFrame Frame,
    ImmutableList<Proposal> Proposals,
    ImmutableHashSet<string> EvidenceFor,
    ImmutableHashSet<string> EvidenceAgainst,
    bool IsOpinion,
    MatchKind Match,
    double MatchScore)
{
    public string Statement => Frame.Render();
}

// ── 實驗：提名 → 登記兩段 ──────────────────────────────────────────────

/// <summary>designer 只能提名。似然以定性等級投票，不是小數。</summary>
public sealed record LikertVote(string ClaimLocalId, ImmutableArray<LikertBelief> ByOutcome);

public sealed record ExperimentDraft(
    string DraftId,
    string Description,
    ImmutableArray<string> Outcomes,
    ImmutableArray<LikertVote> Votes,
    string NominatedBy,
    double EstimatedCost);

/// <summary>
/// 已登記的實驗。似然表由確定性程式（Likert 查表；Phase 2 改為歷史頻率優先）定案，
/// 並附 hash：L4 執行前比對，不符即拒跑。沒有 hash 前置比對，pre-registration 只是口號。
/// </summary>
public sealed record Experiment(
    string Id,
    string Description,
    ImmutableArray<string> Outcomes,
    ImmutableDictionary<string, ImmutableArray<double>> LikelihoodByClaim,
    ImmutableDictionary<string, ImmutableArray<LikertBelief>> LikertByClaim,
    string NominatedBy,
    string RegisteredBy,
    string LikelihoodHash,
    string LikelihoodSource,              // likert_median | catalog_frequency
    int? ObservedOutcome)
{
    /// <summary>hash 只涵蓋「跑實驗之前就該定案」的部分：描述、結果欄、似然表。</summary>
    public static string ComputeHash(string description, ImmutableArray<string> outcomes,
                                     ImmutableDictionary<string, ImmutableArray<double>> likelihoods)
    {
        var sb = new StringBuilder();
        sb.Append(ClaimFrame.Norm(description)).Append("::");
        foreach (var o in outcomes) sb.Append(ClaimFrame.Norm(o)).Append('/');
        sb.Append("::");
        foreach (var kv in likelihoods.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=');
            foreach (var p in kv.Value) sb.Append(p.ToString("F4")).Append(',');
            sb.Append('/');
        }
        return ClaimFrame.Hash(sb.ToString());
    }

    public string RecomputeHash() => ComputeHash(Description, Outcomes, LikelihoodByClaim);
}
