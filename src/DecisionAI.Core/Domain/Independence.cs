// ============================================================================
//  IndependenceBudget 與降級階梯（Rev2 D10 / §4.4）
//
//  多 LLM 的價值完全來自錯誤獨立性。獨立性不可得時，多 agent 只是把成本乘以 N，
//  還會製造虛假信心——三個一致的答案看起來很可信，但若三者同源，那個一致性沒有資訊量。
//  正確行為是退回「單一 agent + 強驗證器」，並讓 Assurance 誠實反映。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public enum DegradationLevel { None, Reduced, Minimal, SingleAgent }

public sealed record IndependenceBudget(
    int DistinctFamilies,
    int DistinctVendors,
    double EffectiveIndependentAgents,   // 依相關性折扣後的有效人數
    bool EnsembleClaimsPermitted)        // 是否准許宣稱集成增益
{
    /// <summary>
    /// 降級階梯只看「有幾個異質 family」這個事實，不看策略模板想要幾個 agent。
    /// 這是安全問題：模型不夠時仍宣稱集成增益，是會誤導決策的失效，不是效能優化。
    /// </summary>
    public static DegradationLevel LevelFor(int distinctFamilies, int poolSize) =>
        poolSize <= 1            ? DegradationLevel.SingleAgent
        : distinctFamilies >= 3  ? DegradationLevel.None
        : distinctFamilies == 2  ? DegradationLevel.Reduced
        :                          DegradationLevel.Minimal;

    public static IndependenceBudget Compute(int families, int vendors, int poolSize)
    {
        var level = LevelFor(families, poolSize);
        // 有效人數：同 family 的第 2 個之後只算半個（粗略的相關性折扣；Phase 3 換成實測相關矩陣）
        double effective = families + Math.Max(0, poolSize - families) * 0.5;
        return new IndependenceBudget(families, vendors, Math.Round(effective, 2),
                                      EnsembleClaimsPermitted: level is DegradationLevel.None or DegradationLevel.Reduced);
    }

    public string Describe() =>
        $"{DistinctFamilies} 個模型家族 / {DistinctVendors} 個廠商，有效獨立 agent {EffectiveIndependentAgents:F1}；" +
        (EnsembleClaimsPermitted ? "准許宣稱集成增益" : "不准宣稱集成增益");
}

public static class Degradation
{
    /// <summary>降級如何影響 solver 數量：不准宣稱集成增益時，多跑 solver 只是把成本乘以 N。</summary>
    public static int CapSolvers(DegradationLevel level, int requested) => level switch
    {
        DegradationLevel.SingleAgent => 1,
        DegradationLevel.Minimal     => Math.Min(requested, 2),
        _                            => requested
    };

    public static string Explain(DegradationLevel level) => level switch
    {
        DegradationLevel.None        => "≥3 異質 family → 完整角色分離",
        DegradationLevel.Reduced     => "2 異質 family → 角色仍分離，但相關性折扣加大",
        DegradationLevel.Minimal     => "單一 family → 靠資訊不對稱維持角色分離；不准宣稱集成增益",
        _                            => "只有 1 個模型 → 禁用所有依賴獨立性的策略，退回單一 agent + 機器驗證"
    };
}

/// <summary>角色指派結果的一格。Phase 1 用約束 + 貪婪；Thompson 抽樣在 Phase 2。</summary>
public sealed record RoleSlot(string Role, string AgentId, string Family, string Vendor);

/// <summary>某個 case 需要哪些角色、各幾個。</summary>
public sealed record RoleDemand(ImmutableArray<(string Role, int Count)> Needs, string Domain);
