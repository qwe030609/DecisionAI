// ============================================================================
//  LocalEmbedder：IEmbedder 的本地實作（hashing trick，不呼叫任何 LLM）。
//
//  刻意不用 LLM：多樣性指標是「守門件」，用被監測的同一批模型來量它自己的
//  多樣性會有明顯的利益衝突，而且會讓 Diversity 變成一條要花錢的路徑。
//  這個實作是確定性的 —— 同樣的文字永遠得到同樣的向量，重放才會 bit-for-bit 相同。
// ============================================================================

using DecisionAI.Core.Ports;

namespace DecisionAI.Adapters.Runtime;

public sealed class LocalEmbedder : IEmbedder
{
    public LocalEmbedder(int dimensions = 256, int nGram = 3)
    { Dimensions = Math.Max(16, dimensions); _n = Math.Max(1, nGram); }

    private readonly int _n;

    public string Name => $"local-hash-{Dimensions}d-{_n}gram";
    public int Dimensions { get; }

    public float[] Embed(string text)
    {
        var v = new float[Dimensions];
        var t = Normalize(text);
        for (int i = 0; i + _n <= t.Length; i++)
        {
            int h = SeededRandomSource.StableHash(t.Substring(i, _n));
            int bucket = (int)((uint)h % (uint)Dimensions);
            // 用雜湊的一個 bit 決定正負號：避免所有特徵同號導致任兩段文字都高度相似
            v[bucket] += ((h >> 31) & 1) == 0 ? 1f : -1f;
        }

        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    private static string Normalize(string s)
        => new string(s.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());
}
