// ============================================================================
//  Catalog — 領域記憶（Rev2 §4.1）
//  把「每次從零猜」變成「在累積的領域知識上做選擇」。
//  ATS 的 AMQP 斷線原因本來就是有限幾類；Catalog 就是那份 FMEA 表的可執行版本，
//  而且會隨著每個 case 的真實 outcome 自我灌溉。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Catalog;

public sealed record CatalogEntry(string CatalogId, ClaimFrame Frame, string Domain, long AddedAtVersion, int ConfirmedTrueCount);

/// <summary>某個版本的唯讀快照。case 全程只看它 pin 的版本。</summary>
public sealed record ClaimCatalogView(long Version, ImmutableArray<CatalogEntry> Entries)
{
    public IEnumerable<CatalogEntry> InDomain(string domain)
        => Entries.Where(e => e.Domain == domain || e.Domain == "*");
}

public interface IClaimCatalog
{
    long CurrentVersion { get; }
    ClaimCatalogView View(long version);
    /// <summary>只有 Evaluation 在 case 結束後能呼叫；執行中不得寫。</summary>
    CatalogEntry Register(ClaimFrame frame, string domain, bool confirmedTrue);
}

/// <summary>
/// 版本化、append-only 的記憶體實作。既有 entry 的語意不可就地修改
/// （否則歷史似然失效），只能新增或累加命中次數。
/// </summary>
public sealed class InMemoryClaimCatalog : IClaimCatalog
{
    private readonly List<CatalogEntry> _entries = new();
    private long _version;
    private readonly object _lock = new();

    public long CurrentVersion { get { lock (_lock) return _version; } }

    public static InMemoryClaimCatalog Seeded(params (string Domain, ClaimFrame Frame)[] seeds)
    {
        var c = new InMemoryClaimCatalog();
        foreach (var (d, f) in seeds) c.Register(f, d, confirmedTrue: false);
        return c;
    }

    public ClaimCatalogView View(long version)
    {
        lock (_lock)
            return new ClaimCatalogView(version, _entries.Where(e => e.AddedAtVersion <= version).ToImmutableArray());
    }

    public CatalogEntry Register(ClaimFrame frame, string domain, bool confirmedTrue)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.Domain == domain && e.Frame.Identity == frame.Identity);
            if (existing is not null)
            {
                if (!confirmedTrue) return existing;
                var bumped = existing with { ConfirmedTrueCount = existing.ConfirmedTrueCount + 1 };
                _entries[_entries.IndexOf(existing)] = bumped;
                return bumped;
            }
            _version++;
            var entry = new CatalogEntry(MakeId(frame, domain), frame, domain, _version, confirmedTrue ? 1 : 0);
            _entries.Add(entry);
            return entry;
        }
    }

    /// <summary>catalog id 由領域 + 機制 + locus 決定，跨 case 穩定且人看得懂。</summary>
    private static string MakeId(ClaimFrame f, string domain)
        => $"CLM-{domain}-{f.Mechanism}-{ClaimFrame.Hash(ClaimFrame.Norm(f.Locus))[..6]}";
}
