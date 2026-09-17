// ============================================================================
//  Canonicalizer — 三段式對映（Rev2 §4.1）
//    Exact     四元組完全相同 → 直接對映
//    Fuzzy     同 Mechanism + 同 Locus，Trigger/Observable 措辭不同 → 對映並記 MatchScore
//    Ambiguous 灰帶 → 升級 arbiter，不自動猜
//  自動猜錯會把兩個不同故障合併成一個，後驗從此錯到底——所以灰帶寧可停下來問人。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Catalog;

public interface IClaimCanonicalizer
{
    CanonicalizationResult Canonicalize(ClaimFrame frame, ClaimCatalogView catalog, string domain);
}

public sealed class ClaimCanonicalizer : IClaimCanonicalizer
{
    /// <summary>Locus 相似度落在這個區間 → 灰帶，交 arbiter。</summary>
    public double AmbiguousLow { get; init; } = 0.34;
    public double AmbiguousHigh { get; init; } = 0.999;

    public CanonicalizationResult Canonicalize(ClaimFrame frame, ClaimCatalogView catalog, string domain)
    {
        var pool = catalog.InDomain(domain).ToList();

        // 1) Exact：四欄正規化後完全相同
        var exact = pool.FirstOrDefault(e => e.Frame.FullIdentity == frame.FullIdentity);
        if (exact is not null) return CanonicalizationResult.Exact(exact.CatalogId);

        // 2) Fuzzy：同 Mechanism + 同 Locus（正規化後），只有 Trigger/Observable 措辭不同
        var fuzzy = pool.FirstOrDefault(e => e.Frame.Identity == frame.Identity);
        if (fuzzy is not null)
        {
            double score = 0.5 + 0.5 * Jaccard(
                Tokens(frame.Trigger + " " + frame.Observable),
                Tokens(fuzzy.Frame.Trigger + " " + fuzzy.Frame.Observable));
            return CanonicalizationResult.Fuzzy(fuzzy.CatalogId, Math.Round(score, 3));
        }

        // 3) Ambiguous：同 Mechanism，Locus 部分重疊 → 可能是同一個故障的不同寫法，也可能是不同故障
        var candidates = pool
            .Where(e => e.Frame.Mechanism == frame.Mechanism)
            .Select(e => (e, sim: Jaccard(Tokens(e.Frame.Locus), Tokens(frame.Locus))))
            .Where(x => x.sim > AmbiguousLow && x.sim < AmbiguousHigh)
            .OrderByDescending(x => x.sim).ThenBy(x => x.e.CatalogId, StringComparer.Ordinal)
            .Select(x => x.e.CatalogId).ToImmutableArray();
        if (candidates.Length > 0) return CanonicalizationResult.Ambiguous(candidates);

        // 4) New：決定性 draft id，等 Evaluation 在 case 結束後提名進 Catalog
        return CanonicalizationResult.NewEntry(frame.DraftId);
    }

    private static ImmutableHashSet<string> Tokens(string s)
        => ClaimFrame.Norm(s)
            .Split(new[] { ' ', '.', '_', '/', '-', '(', ')', ':', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .ToImmutableHashSet();

    private static double Jaccard(ImmutableHashSet<string> a, ImmutableHashSet<string> b)
        => a.Count == 0 && b.Count == 0 ? 1 : a.Intersect(b).Count / (double)a.Union(b).Count;
}

/// <summary>Mutation switch：永遠回傳新條目 → 跨 case 記憶失效、主張 id 不穩定。</summary>
public sealed class AlwaysNewCanonicalizer : IClaimCanonicalizer
{
    public CanonicalizationResult Canonicalize(ClaimFrame frame, ClaimCatalogView catalog, string domain)
        => CanonicalizationResult.NewEntry(frame.DraftId);
}
