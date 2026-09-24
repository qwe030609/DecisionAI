// ============================================================================
//  Diversity（Rev2 §4.11 / Phase 2）：mode collapse 偵測與階梯處置。
//
//  三條不可妥協的規則：
//   1) 多指標並用——只看單一指標一定會被鑽漏洞。
//   2) 門檻預先登記——不可在看到結果後調整（與實驗似然同樣的反 Goodhart 邏輯）。
//   3) 處置第 2 階必須換異質 vendor/family，不是同模型重抽——同模型重抽解決不了模式崩塌。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Diversity;

public sealed record Candidate(string Id, string Text, string AgentId, string Family);

public interface IDiversityMetric
{
    string Name { get; }
    /// <summary>0 = 完全同質，1 = 完全分歧。</summary>
    double Score(IReadOnlyList<Candidate> candidates);
}

/// <summary>不重複 n-gram 的比例：最便宜的表面多樣性指標。</summary>
public sealed class DistinctNGram : IDiversityMetric
{
    public int N { get; init; } = 3;
    public string Name => $"distinct-{N}gram";

    public double Score(IReadOnlyList<Candidate> c)
    {
        var all = c.SelectMany(x => Grams(x.Text, N)).ToList();
        return all.Count == 0 ? 0 : all.Distinct().Count() / (double)all.Count;
    }

    internal static IEnumerable<string> Grams(string s, int n)
    {
        var t = ClaimFrame.Norm(s).Replace(" ", "");
        for (int i = 0; i + n <= t.Length; i++) yield return t.Substring(i, n);
    }
}

/// <summary>1 − 平均 self-BLEU（以 n-gram 重疊近似）：兩兩之間互相抄得越兇，分數越低。</summary>
public sealed class InverseSelfBleu : IDiversityMetric
{
    public int N { get; init; } = 3;
    public string Name => "inverse-self-bleu";

    public double Score(IReadOnlyList<Candidate> c)
    {
        if (c.Count < 2) return 0;
        double total = 0; int pairs = 0;
        for (int i = 0; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++)
            {
                var a = DistinctNGram.Grams(c[i].Text, N).ToHashSet();
                var b = DistinctNGram.Grams(c[j].Text, N).ToHashSet();
                if (a.Count == 0 || b.Count == 0) continue;
                total += a.Intersect(b).Count() / (double)Math.Min(a.Count, b.Count);
                pairs++;
            }
        return pairs == 0 ? 0 : 1 - total / pairs;
    }
}

/// <summary>嵌入空間的平均兩兩距離。IEmbedder 是獨立 port，可用本地小模型。</summary>
public sealed class EmbeddingPairwise : IDiversityMetric
{
    private readonly IEmbedder _embedder;
    public EmbeddingPairwise(IEmbedder embedder) => _embedder = embedder;
    public string Name => "embedding-pairwise";

    public double Score(IReadOnlyList<Candidate> c)
    {
        if (c.Count < 2) return 0;
        var vecs = c.Select(x => _embedder.Embed(x.Text)).ToList();
        double total = 0; int pairs = 0;
        for (int i = 0; i < vecs.Count; i++)
            for (int j = i + 1; j < vecs.Count; j++) { total += 1 - Cosine(vecs[i], vecs[j]); pairs++; }
        return pairs == 0 ? 0 : Math.Clamp(total / pairs, 0, 1);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>語意群數 / 候選數：把近義的候選併成一群，看還剩幾個真正不同的點子。</summary>
public sealed class SemanticClusterCount : IDiversityMetric
{
    public double MergeAt { get; init; } = 0.6;      // n-gram 重疊超過這個比例視為同一群
    public string Name => "semantic-clusters";

    public double Score(IReadOnlyList<Candidate> c)
    {
        if (c.Count == 0) return 0;
        var reps = new List<HashSet<string>>();
        foreach (var x in c)
        {
            var g = DistinctNGram.Grams(x.Text, 3).ToHashSet();
            if (g.Count == 0) continue;
            if (!reps.Any(r => r.Intersect(g).Count() / (double)Math.Min(r.Count, g.Count) >= MergeAt)) reps.Add(g);
        }
        return reps.Count / (double)c.Count;
    }
}

// ── 監測與處置 ─────────────────────────────────────────────────────────

/// <summary>門檻必須預先登記，不可在看到結果後調整。</summary>
public sealed record DiversityThresholds(ImmutableDictionary<string, double> MinByMetric)
{
    public static DiversityThresholds Default => new(ImmutableDictionary<string, double>.Empty
        .Add("distinct-3gram", 0.45)
        .Add("inverse-self-bleu", 0.40)
        .Add("semantic-clusters", 0.50));

    /// <summary>降級到 Minimal 時門檻自動提高：同 family 的表面差異不代表真多樣性。</summary>
    public DiversityThresholds Tightened(double by = 0.15)
        => new(MinByMetric.ToImmutableDictionary(k => k.Key, k => Math.Min(0.95, k.Value + by)));
}

public sealed record DiversityVerdict(bool Collapsed, ImmutableDictionary<string, double> Scores,
                                      ImmutableArray<string> FailedMetrics, string Detail);

public interface IDiversityMonitor
{
    DiversityVerdict Evaluate(IReadOnlyList<Candidate> candidates, DiversityThresholds preRegistered);
}

public sealed class DiversityMonitor : IDiversityMonitor
{
    private readonly IReadOnlyList<IDiversityMetric> _metrics;
    public DiversityMonitor(params IDiversityMetric[] metrics)
        => _metrics = metrics.Length > 0 ? metrics
            : new IDiversityMetric[] { new DistinctNGram(), new InverseSelfBleu(), new SemanticClusterCount() };

    public DiversityVerdict Evaluate(IReadOnlyList<Candidate> candidates, DiversityThresholds pre)
    {
        var scores = _metrics.ToImmutableDictionary(m => m.Name, m => Math.Round(m.Score(candidates), 3));
        var failed = scores.Where(kv => pre.MinByMetric.TryGetValue(kv.Key, out var min) && kv.Value < min)
                           .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        string detail = string.Join("；", scores.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} {kv.Value:F2}" + (pre.MinByMetric.TryGetValue(kv.Key, out var m) ? $"（門檻 {m:F2}）" : "")));
        return new DiversityVerdict(failed.Length > 0, scores, failed, detail);
    }
}

/// <summary>Mutation switch：永遠說沒崩塌。</summary>
public sealed class AlwaysPassMonitor : IDiversityMonitor
{
    public DiversityVerdict Evaluate(IReadOnlyList<Candidate> candidates, DiversityThresholds pre)
        => new(false, ImmutableDictionary<string, double>.Empty, ImmutableArray<string>.Empty, "（壞的守門件）一律通過");
}

public enum RemedyKind { None, RaiseTemperature, SwitchFamily, InjectDiversityPrompt, FacilityLocationSubset, GiveUp }

public sealed record RemedyStep(RemedyKind Kind, string Instruction, double? Temperature = null);

public interface IDiversityRemedy
{
    RemedyStep Next(DiversityVerdict v, int attempt);
}

/// <summary>階梯處置。第 2 階必須換異質 vendor/family——同模型重抽解決不了模式崩塌。</summary>
public sealed class DiversityRemedy : IDiversityRemedy
{
    public RemedyStep Next(DiversityVerdict v, int attempt) => (v.Collapsed, attempt) switch
    {
        (false, _) => new RemedyStep(RemedyKind.None, "多樣性達標，不需處置"),
        (true, 0)  => new RemedyStep(RemedyKind.RaiseTemperature, "第 1 階：提高 temperature / top-p 後重抽", 0.9),
        (true, 1)  => new RemedyStep(RemedyKind.SwitchFamily, "第 2 階：換異質 vendor / family 重抽（同模型重抽解決不了模式崩塌）"),
        (true, 2)  => new RemedyStep(RemedyKind.InjectDiversityPrompt, "第 3 階：注入多樣性條件（要求與既有候選在機制上不同）"),
        (true, 3)  => new RemedyStep(RemedyKind.FacilityLocationSubset, "第 4 階：facility-location 貪婪挑出最不像的子集"),
        _          => new RemedyStep(RemedyKind.GiveUp, "階梯用盡仍崩塌 → 交人並在 Assurance 標示多樣性不足")
    };

    /// <summary>facility-location 貪婪：從候選裡挑出彼此最不像的 k 個。</summary>
    public static ImmutableArray<Candidate> PickDiverseSubset(IReadOnlyList<Candidate> candidates, int k)
    {
        if (candidates.Count <= k) return candidates.ToImmutableArray();
        var chosen = new List<Candidate> { candidates.OrderBy(c => c.Id, StringComparer.Ordinal).First() };
        var rest = candidates.Except(chosen).ToList();
        while (chosen.Count < k && rest.Count > 0)
        {
            var next = rest.OrderByDescending(r => chosen.Min(c => Distance(c.Text, r.Text)))
                           .ThenBy(r => r.Id, StringComparer.Ordinal).First();
            chosen.Add(next); rest.Remove(next);
        }
        return chosen.ToImmutableArray();
    }

    private static double Distance(string a, string b)
    {
        var ga = DistinctNGram.Grams(a, 3).ToHashSet();
        var gb = DistinctNGram.Grams(b, 3).ToHashSet();
        if (ga.Count == 0 || gb.Count == 0) return 1;
        return 1 - ga.Intersect(gb).Count() / (double)ga.Union(gb).Count();
    }
}
