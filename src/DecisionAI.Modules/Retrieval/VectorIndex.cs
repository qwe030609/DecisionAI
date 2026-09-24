// ============================================================================
//  向量檢索 port（Rev2 §8「持久化躲在 port 後面」/ Phase 3）
//
//  Catalog 大到一定程度之後，Canonicalizer 的線性掃描就不能用了：
//  它現在對每一條主張掃過整個領域的條目。向量索引把「先找出像的幾條」
//  與「判定是不是同一條」分開——前者可以近似，後者絕不可以。
//
//  這個分工很重要：檢索只負責把候選縮小，最終判定仍然走
//  Exact / Fuzzy / Ambiguous 三段式與四元組比對。近似檢索絕不可以直接當成對映，
//  否則「相似度 0.83」就會悄悄變成「這是同一個故障」——那是後驗從此錯到底的起點。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Catalog;

namespace DecisionAI.Modules.Retrieval;

public sealed record VectorHit(string Id, double Similarity, string Domain);

public interface IVectorIndex
{
    string Name { get; }
    void Upsert(string id, string domain, string text);
    /// <summary>回傳最像的 k 筆。只用來縮小候選，不得當成對映結論。</summary>
    ImmutableArray<VectorHit> Search(string text, string domain, int k);
    int Count { get; }
}

/// <summary>
/// 記憶體實作：餘弦相似度全掃。小規模夠用，而且完全確定性——
/// 重放測試要 bit-for-bit 相同，近似最近鄰的隨機性會直接破壞這件事。
/// </summary>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly IEmbedder _embedder;
    private readonly Dictionary<string, (float[] Vec, string Domain)> _items = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public InMemoryVectorIndex(IEmbedder embedder) => _embedder = embedder;

    public string Name => $"in-memory/{_embedder.Name}";
    public int Count { get { lock (_lock) return _items.Count; } }

    public void Upsert(string id, string domain, string text)
    {
        var v = _embedder.Embed(text);
        lock (_lock) _items[id] = (v, domain);
    }

    public ImmutableArray<VectorHit> Search(string text, string domain, int k)
    {
        var q = _embedder.Embed(text);
        List<KeyValuePair<string, (float[] Vec, string Domain)>> snapshot;
        lock (_lock) snapshot = _items.Where(kv => kv.Value.Domain == domain).ToList();

        return snapshot
            .Select(kv => new VectorHit(kv.Key, Math.Round(Cosine(q, kv.Value.Vec), 6), kv.Value.Domain))
            .OrderByDescending(h => h.Similarity)
            .ThenBy(h => h.Id, StringComparer.Ordinal)        // 同分時以 id 排序：否則順序隨 Dictionary 而變
            .Take(Math.Max(1, k))
            .ToImmutableArray();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
/// 用向量索引先縮小候選，再交給真正的 Canonicalizer 判定。
/// 索引壞掉或沒建時直接退回全掃——檢索是效能設施，不是正確性設施，
/// 所以它的失效只能讓系統變慢，不能讓系統判錯。
/// </summary>
public sealed class RetrievalAssistedCanonicalizer : IClaimCanonicalizer
{
    private readonly IClaimCanonicalizer _inner;
    private readonly IVectorIndex _index;

    public RetrievalAssistedCanonicalizer(IClaimCanonicalizer inner, IVectorIndex index)
    { _inner = inner; _index = index; }

    /// <summary>候選數。取太小會漏掉正確條目，所以寧可寬一點——縮小候選的目的是省時間，不是做決定。</summary>
    public int TopK { get; init; } = 16;

    public CanonicalizationResult Canonicalize(ClaimFrame frame, ClaimCatalogView catalog, string domain)
    {
        if (_index.Count == 0) return _inner.Canonicalize(frame, catalog, domain);

        var keep = _index.Search(frame.Render(), domain, TopK).Select(h => h.Id).ToImmutableHashSet();
        var narrowed = new ClaimCatalogView(catalog.Version,
            catalog.Entries.Where(e => keep.Contains(e.CatalogId)).ToImmutableArray());

        // 縮小後找不到任何東西 → 退回全掃，而不是回報「新條目」
        var result = _inner.Canonicalize(frame, narrowed, domain);
        return result.Kind == MatchKind.New ? _inner.Canonicalize(frame, catalog, domain) : result;
    }
}
