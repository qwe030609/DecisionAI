// ============================================================================
//  正式環境的時間與亂數 port。
// ============================================================================

using DecisionAI.Core.Ports;

namespace DecisionAI.Adapters.Runtime;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>固定種子；Fork(label) 用 label 的穩定雜湊派生子流（不用 string.GetHashCode：每個 process 不同）。</summary>
public sealed class SeededRandomSource : IRandomSource
{
    private readonly Random _rng;
    private readonly int _seed;
    private readonly object _lock = new();

    public SeededRandomSource(int seed) { _seed = seed; _rng = new Random(seed); }

    public double NextDouble() { lock (_lock) return _rng.NextDouble(); }
    public int Next(int max) { lock (_lock) return _rng.Next(max); }
    public IRandomSource Fork(string label) => new SeededRandomSource(unchecked(_seed * 31 + StableHash(label)));

    public static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }   // FNV-1a
            return (int)h;
        }
    }
}
