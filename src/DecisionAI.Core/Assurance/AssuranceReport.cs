// ============================================================================
//  Assurance — 三層 + 拒絕通道。三層永遠分開保留，絕不相乘成一個數字。
//  它不屬於 Decision：根本沒有決策可做時仍然要有輸出（拒答理由）。
// ============================================================================

using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Assurance;

public enum ReasonCode
{
    None,
    NoVerifier,                 // Triage 第一問：沒有 L3+ 的 verifier
    NoGroundTruth,              // Triage 第二問：沒有各方同意的 ground truth
    DefinitionalDispute,        // Triage 第三問：爭議在定義本身
    ReflexiveDirection,         // 反身系統 → 禁止方向預測
    DelegatedToExternalModel,   // Tool/Model Router 轉介（Phase 2）
    InsufficientCoverage,       // 覆蓋層不足（Phase 2 conformal）
    PipelineHalted,             // 邊界攔截 / 人類拒絕 → 流程中止
    NoAdmissibleAction          // 所有行動都超過可接受損失 → 交人
}

/// <summary>能力層：通過的最高驗證等級與其上限。Degraded = 有步驟以部分結果降級完成。</summary>
public sealed record CapabilityLayer(VerifierLevel BestPassedLevel, double Cap, bool Degraded);

/// <summary>覆蓋層：conformal 集合 / 區間。Phase 1 一律 null。</summary>
public sealed record CoverageLayer(double TargetCoverage, IReadOnlyList<string> PredictionSet);

/// <summary>決策穩定層：來自 Decision 引擎的確定性指標。無效用矩陣時為 null。</summary>
public sealed record StabilityLayer(double Robustness, double WorstCase, double MaxRegret);

public sealed record AbstentionLayer(bool Abstained, ReasonCode Code, string Explanation, string? Alternative)
{
    public static readonly AbstentionLayer NotAbstained = new(false, ReasonCode.None, "", null);
}

public sealed record AssuranceReport(
    CapabilityLayer? Capability,     // 拒答時為 null：不輸出任何信心相關數值
    CoverageLayer?   Coverage,
    StabilityLayer?  Stability,
    AbstentionLayer  Abstention)
{
    /// <summary>UI 用的保守等第：取三層最弱者。內部永遠保留三層，這只是顯示。</summary>
    public string ConservativeGrade()
    {
        if (Abstention.Abstained) return $"ABSTAIN({Abstention.Code})";
        var parts = new List<string>();
        if (Capability is { } c) parts.Add($"cap≤{c.Cap:F2}{(c.Degraded ? "(degraded)" : "")}");
        if (Coverage  is { } v) parts.Add($"cover={v.TargetCoverage:P0}");
        if (Stability is { } s) parts.Add($"robust={s.Robustness:P0}");
        return parts.Count == 0 ? "NO-ASSURANCE" : string.Join(" | ", parts);
    }
}
