// ============================================================================
//  Claim / Experiment — Probabilistic Zone 的產物，但型別是不可變的：
//  進了 Journal 之後只能透過新事件「附加」，不能竄改。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public enum ClaimKind { Hypothesis, Candidate, Forecast }

public sealed record Proposal(string AgentId, double StatedConfidence);

/// <summary>Claim：Agent 提出的東西。同一句話被多個 Agent 提出時合併成一個 Claim（合併由 reducer 依 ClaimId 進行）。</summary>
public sealed record Claim(
    string Id,
    ClaimKind Kind,
    string Statement,
    ImmutableList<Proposal> Proposals,
    ImmutableHashSet<string> EvidenceFor,
    ImmutableHashSet<string> EvidenceAgainst,
    bool IsOpinion)                       // L2 規則：一條證據都沒引用 → 意見，不進機率引擎
{
    public static string Normalize(string s)
        => string.Join(" ", s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static ImmutableHashSet<string> Tokens(string s) => Normalize(s).Split(' ').ToImmutableHashSet();

    public static double Jaccard(ImmutableHashSet<string> a, ImmutableHashSet<string> b)
        => a.Count == 0 && b.Count == 0 ? 1 : a.Intersect(b).Count / (double)a.Union(b).Count;
}

/// <summary>
/// 實驗：預先登記（pre-registration）。似然表 P(outcome | claim) 在跑實驗「之前」寫下，
/// 之後 Bayesian 更新只能用登記過的欄；事件不可變 → 觀察後無法竄改似然。
/// </summary>
public sealed record Experiment(
    string Id,
    string Description,
    ImmutableArray<string> Outcomes,
    ImmutableDictionary<string, ImmutableArray<double>> LikelihoodByClaim,
    string DesignedBy,
    int? ObservedOutcome);
