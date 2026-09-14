// ============================================================================
//  Evidence Store — 「AI 到底根據什麼得到這個結論？」
//  證據 ≠ 意見。每個 Claim 必須引用 Evidence ID；引用不存在或沒引用，L2 規則會抓。
//  正式版：PostgreSQL（case / claim / agent_run / verification）+ pgvector（語義檢索）+ 物件儲存。
//  這裡用記憶體版 + 詞彙重疊檢索，介面不變。
// ============================================================================

using DecisionAI.Domain;

namespace DecisionAI.Store;

public sealed class EvidenceStore
{
    private readonly Dictionary<string, Evidence> _byId = new();
    private readonly object _lock = new();

    public Evidence Add(EvidenceSource source, string origin, string content, DateTime? at = null)
    {
        lock (_lock)
        {
            var ev = new Evidence($"EV-{_byId.Count + 1:000}", source, origin, content, at ?? DateTime.UtcNow);
            _byId[ev.Id] = ev;
            return ev;
        }
    }

    public bool Exists(string id) { lock (_lock) return _byId.ContainsKey(id); }
    public Evidence? Get(string id) { lock (_lock) return _byId.GetValueOrDefault(id); }

    /// <summary>檢索：詞彙重疊排序。正式版換成向量檢索，呼叫端不變。</summary>
    public IReadOnlyList<Evidence> Search(string query, int k)
    {
        var q = Tokens(query);
        lock (_lock)
            return _byId.Values
                .Select(e => (e, score: Tokens(e.Content + " " + e.Origin).Intersect(q).Count() * e.Reliability))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(k).Select(x => x.e).ToList();
    }

    public IReadOnlyList<string> UnknownCitations(Claim c)
        => c.EvidenceFor.Concat(c.EvidenceAgainst).Where(id => !Exists(id)).Distinct().ToList();

    public static string RenderForPrompt(IEnumerable<Evidence> evidence)
        => string.Join("\n", evidence.Select(e => $"[{e.Id}] ({e.Source}, r={e.Reliability:F2}, {e.Origin}) {e.Content}"));

    private static HashSet<string> Tokens(string s)
        => s.ToLowerInvariant().Split(new[] { ' ', '，', '。', '、', ',', '.', ':', '：', '(', ')', '（', '）', '\n' },
            StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}
