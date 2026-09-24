// ============================================================================
//  ExperimentCatalog（Rev2 §4.1 / Phase 2）——跨 case 的實驗記憶。
//  在「已知真假設」的條件下觀察到什麼結果 → 累積成歷史頻率。
//  這是似然最硬的來源：它不是估出來的，是數出來的，而且隨系統使用自我灌溉。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Modules.Catalog;

/// <summary>某個實驗在某個真假設下的結果次數。</summary>
public sealed record HistoricalLikelihood(string ExperimentKey, string ClaimCatalogId, ImmutableArray<int> OutcomeCounts)
{
    public int Total => OutcomeCounts.Sum();

    /// <summary>Laplace 平滑後的頻率，避免 0 次觀察直接把後驗打死。</summary>
    public ImmutableArray<double> Frequencies
        => OutcomeCounts.Select(c => (c + 1.0) / (Total + OutcomeCounts.Length)).ToImmutableArray();
}

public sealed record ExperimentCatalogView(long Version, ImmutableArray<HistoricalLikelihood> Entries)
{
    public IEnumerable<HistoricalLikelihood> For(string experimentKey)
        => Entries.Where(e => e.ExperimentKey == experimentKey);
}

public interface IExperimentCatalog
{
    long CurrentVersion { get; }
    ExperimentCatalogView View(long version);
    /// <summary>只有 Evaluation 在 case 結束後能呼叫：真假設已知時才算得出這一格。</summary>
    void Record(string experimentKey, string claimCatalogId, int outcomeIndex, int outcomeCount);
}

public sealed class InMemoryExperimentCatalog : IExperimentCatalog
{
    private readonly Dictionary<(string, string), int[]> _counts = new();
    private long _version;
    private readonly object _lock = new();

    public long CurrentVersion { get { lock (_lock) return _version; } }

    /// <summary>實驗的識別：用正規化後的描述，讓「同一個實驗換句話說」仍然對得上。</summary>
    public static string KeyOf(string description) => DecisionAI.Core.Domain.ClaimFrame.Hash(
        DecisionAI.Core.Domain.ClaimFrame.Norm(description))[..12];

    public ExperimentCatalogView View(long version)
    {
        lock (_lock)
            return new ExperimentCatalogView(version, _counts
                .Select(kv => new HistoricalLikelihood(kv.Key.Item1, kv.Key.Item2, kv.Value.ToImmutableArray()))
                .OrderBy(e => e.ExperimentKey, StringComparer.Ordinal)
                .ThenBy(e => e.ClaimCatalogId, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    public void Record(string experimentKey, string claimCatalogId, int outcomeIndex, int outcomeCount)
    {
        lock (_lock)
        {
            var key = (experimentKey, claimCatalogId);
            if (!_counts.TryGetValue(key, out var arr) || arr.Length != outcomeCount)
                _counts[key] = arr = new int[outcomeCount];
            if (outcomeIndex >= 0 && outcomeIndex < arr.Length) arr[outcomeIndex]++;
            _version++;
        }
    }

    /// <summary>測試與示範用：直接播種一段歷史。</summary>
    public InMemoryExperimentCatalog Seed(string description, string claimCatalogId, params int[] counts)
    {
        lock (_lock) { _counts[(KeyOf(description), claimCatalogId)] = counts.ToArray(); _version++; }
        return this;
    }
}
