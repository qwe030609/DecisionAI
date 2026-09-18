// ============================================================================
//  三層錯誤相關性（Rev2 §4.3）——確定性區，不得出現 ILlm。
//    LineagePrior       同家族 / 同廠商 → 高先驗（冷啟動時唯一能用的資訊）
//    ProbeCalibrated    跨 case 累積的實測 phi（共同案例 < 3 視為無資訊）
//    InstanceDivergence 本案輸出的即時分歧：兩人提出的主張集合幾乎一樣 → 這一次高度相關
//  依樣本量收縮合成：證據少時信先驗，證據多時信實測。
//
//  為什麼重要：多 LLM 的價值完全來自錯誤獨立性。三個一致的答案看起來很可信，
//  但若三者同源，那個一致性沒有任何資訊量——集成權重必須據此打折。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;

namespace DecisionAI.Modules.Probability;

public sealed record CorrelationContext(
    ImmutableDictionary<string, AgentLineage> Lineage,
    ImmutableDictionary<string, ImmutableHashSet<string>> ClaimsByAgent,
    PolicySnapshot Policy);

public sealed record AgentLineage(string Vendor, string Family, string Generation);

public interface ICorrelationEstimator
{
    string Name { get; }
    /// <summary>回傳 (估計值, 有效樣本數)。樣本數 0 表示這一層沒有資訊。</summary>
    (double Rho, int Samples) Estimate(string a, string b, CorrelationContext ctx);
}

/// <summary>第一層：譜系先驗。冷啟動時唯一能用的資訊，也是最容易被忽略的一種相關性。</summary>
public sealed class LineagePriorEstimator : ICorrelationEstimator
{
    public string Name => "lineage";
    public double SameFamily { get; init; } = 0.70;
    public double SameVendor { get; init; } = 0.40;
    public double Unrelated { get; init; } = 0.15;

    public (double, int) Estimate(string a, string b, CorrelationContext ctx)
    {
        if (!ctx.Lineage.TryGetValue(a, out var la) || !ctx.Lineage.TryGetValue(b, out var lb)) return (Unrelated, 1);
        if (la.Family == lb.Family) return (SameFamily, 1);
        if (la.Vendor == lb.Vendor) return (SameVendor, 1);
        return (Unrelated, 1);
    }
}

/// <summary>第二層：跨 case 累積的實測相關。共同案例少於 3 → 視為無資訊（保留 Rev1 的邊界）。</summary>
public sealed class ProbeCalibratedEstimator : ICorrelationEstimator
{
    public string Name => "probe";
    public int MinShared { get; init; } = 3;

    public (double, int) Estimate(string a, string b, CorrelationContext ctx)
    {
        var (phi, shared) = ctx.Policy.Correlation.Get(a, b);
        return shared < MinShared ? (0, 0) : (Math.Clamp(phi, -1, 1), shared);
    }
}

/// <summary>
/// 第三層：本案的即時分歧。兩個 agent 提出的 canonical 主張集合越像，這一次就越相關。
/// 這一層抓得到「共享證據誘發的相關」——那不是模型本質相關，但對集成增益一樣是壞消息。
/// </summary>
public sealed class InstanceDivergenceEstimator : ICorrelationEstimator
{
    public string Name => "instance";

    public (double, int) Estimate(string a, string b, CorrelationContext ctx)
    {
        if (!ctx.ClaimsByAgent.TryGetValue(a, out var ca) || !ctx.ClaimsByAgent.TryGetValue(b, out var cb)) return (0, 0);
        if (ca.Count == 0 || cb.Count == 0) return (0, 0);
        double jaccard = ca.Intersect(cb).Count / (double)ca.Union(cb).Count;
        return (jaccard, Math.Min(ca.Count, cb.Count));
    }
}

/// <summary>依樣本量收縮合成：實測樣本越多，先驗的份量越輕。</summary>
public sealed class BlendedCorrelation : ICorrelationEstimator
{
    private readonly ICorrelationEstimator _lineage, _probe, _instance;
    public string Name => "blended";
    /// <summary>實測要累積到這個量才完全取代先驗。</summary>
    public double ProbeSaturation { get; init; } = 10;

    public BlendedCorrelation(ICorrelationEstimator? lineage = null, ICorrelationEstimator? probe = null,
                              ICorrelationEstimator? instance = null)
    {
        _lineage = lineage ?? new LineagePriorEstimator();
        _probe = probe ?? new ProbeCalibratedEstimator();
        _instance = instance ?? new InstanceDivergenceEstimator();
    }

    public (double Rho, int Samples) Estimate(string a, string b, CorrelationContext ctx)
    {
        if (a == b) return (1.0, int.MaxValue);
        var (lp, _) = _lineage.Estimate(a, b, ctx);
        var (pr, pn) = _probe.Estimate(a, b, ctx);
        var (inst, insN) = _instance.Estimate(a, b, ctx);

        double wProbe = Math.Min(1.0, pn / ProbeSaturation);
        double baseline = wProbe * pr + (1 - wProbe) * lp;

        // 本案的即時分歧只能往上調（同意度異常高時），不能用來宣稱「這次比較獨立」
        double rho = insN == 0 ? baseline : Math.Max(baseline, inst);
        return (Math.Clamp(rho, 0, 1), pn + insN);
    }

    /// <summary>從 CaseState 組出估計所需的上下文。</summary>
    public static CorrelationContext ContextFrom(CaseState s, PolicySnapshot policy)
    {
        var lineage = s.Roles.GroupBy(r => r.AgentId)
            .ToImmutableDictionary(g => g.Key, g => new AgentLineage(g.First().Vendor, g.First().Family, "-"));

        var byAgent = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>();
        foreach (var claim in s.Claims.Where(c => c.Kind == ClaimKind.Hypothesis))
            foreach (var p in claim.Proposals)
                byAgent[p.AgentId] = byAgent.GetValueOrDefault(p.AgentId, ImmutableHashSet<string>.Empty).Add(claim.Key.Value);

        return new CorrelationContext(lineage, byAgent.ToImmutable(), policy);
    }
}

/// <summary>Mutation switch：假裝所有 agent 完全獨立 → 集成增益被高估。</summary>
public sealed class ZeroCorrelation : ICorrelationEstimator
{
    public string Name => "zero";
    public (double, int) Estimate(string a, string b, CorrelationContext ctx) => (a == b ? 1 : 0, 1);
}
