// ============================================================================
//  PolicySnapshot — 學習狀態與決策讀取解耦（D7）。
//  一個 case 從頭到尾只讀它 pin 的那一份；Evaluation 只產生新版本。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Policy;

/// <summary>Beta(a, b) 後驗。先驗 Beta(1,1)：樣本少時自然收縮到 0.5。</summary>
public sealed record BetaPosterior(double A = 1, double B = 1)
{
    public int N => (int)(A + B - 2);
    public double Mean => A / (A + B);
    public BetaPosterior Observe(bool success) => success ? this with { A = A + 1 } : this with { B = B + 1 };
    public static readonly BetaPosterior Prior = new();
}

public readonly record struct WeightKey(string Agent, string Domain, string Role);

/// <summary>
/// Rev2：Catalog 版本一併凍結。否則同一個 case 執行中途 Catalog 被別人更新，
/// canonical id 就不決定性了。
/// </summary>
public sealed record PolicySnapshot(long Version, ImmutableDictionary<WeightKey, BetaPosterior> Weights,
                                    long ClaimCatalogVersion = 0)
{
    public static readonly PolicySnapshot Initial = new(0, ImmutableDictionary<WeightKey, BetaPosterior>.Empty, 0);

    public BetaPosterior Weight(string agent, string domain, string role)
        => Weights.GetValueOrDefault(new WeightKey(agent, domain, role), BetaPosterior.Prior);

    public PolicySnapshot Apply(PolicyDelta delta)
    {
        var b = Weights.ToBuilder();
        foreach (var o in delta.Observations.Where(o => VerifierTrust.UpdatesWeights(o.Level)))
        {
            var key = new WeightKey(o.Agent, o.Domain, o.Role);
            b[key] = b.GetValueOrDefault(key, BetaPosterior.Prior).Observe(o.Success);
        }
        return new PolicySnapshot(Version + 1, b.ToImmutable(), delta.CatalogVersionAfter ?? ClaimCatalogVersion);
    }

    public string Report()
    {
        var lines = new List<string> { $"policy v{Version}（claim catalog v{ClaimCatalogVersion}）", "agent            domain/role                      mean   n" };
        foreach (var kv in Weights.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Role))
            lines.Add($"{kv.Key.Agent,-16} {kv.Key.Domain + "/" + kv.Key.Role,-32} {kv.Value.Mean:F2}   {kv.Value.N}");
        return string.Join("\n", lines);
    }
}

public sealed record WeightObservation(string Agent, string Domain, string Role, bool Success, VerifierLevel Level);

/// <summary>Evaluation 的產物：只描述「觀察到什麼」，不直接寫入。</summary>
public sealed record CatalogEntryCandidate(string DraftId, ClaimFrame Frame, string Domain, bool ConfirmedTrue);

public sealed record PolicyDelta(string CaseId, ImmutableArray<WeightObservation> Observations,
                                 ImmutableArray<CatalogEntryCandidate> CatalogEntries, ImmutableArray<string> Notes,
                                 long? CatalogVersionAfter = null);
