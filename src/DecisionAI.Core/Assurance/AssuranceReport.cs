// ============================================================================
//  Assurance — 三層 + 拒絕通道。三層永遠分開保留，絕不相乘成一個數字。
//  Rev2：AbstentionLayer 帶型別化 payload；報告新增 LikelihoodSensitive 與假設覆蓋率欄位
//  （Phase 1 只有型別與旗標，Chao1 與三擾動 Stability 在 Phase 2）。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Assurance;

/// <summary>能力層：通過的最高驗證等級與其上限。Degraded 記錄降級原因（timeout 部分結果、獨立性不足）。</summary>
public sealed record CapabilityLayer(
    VerifierLevel BestPassedLevel, double Cap, bool Degraded, ImmutableArray<string> DegradedReasons)
{
    public static CapabilityLayer Of(VerifierLevel level, params string[] reasons)
        => new(level, VerifierTrust.Cap(level), reasons.Length > 0, reasons.ToImmutableArray());
}

/// <summary>覆蓋層：conformal 集合 / 區間。Phase 1 一律 null。</summary>
public sealed record CoverageLayer(double TargetCoverage, ImmutableArray<string> PredictionSet);

/// <summary>
/// 決策穩定層。Phase 1 只有機率擾動；假設集重抽樣與似然 ±1 級在 Phase 2
/// （對應 Rev2 §4.10 的三擾動表，欄位先留著讓報告形狀固定）。
/// </summary>
public sealed record StabilityLayer(
    double Robustness, double WorstCase, double MaxRegret,
    double? DecisionStabilityUnderResampling = null,
    bool? PosteriorOrderStableUnderLikertShift = null);

public sealed record AbstentionLayer(
    bool Abstained, ReasonCode Code, string Explanation, SubstitutePayload? Payload)
{
    public static readonly AbstentionLayer NotAbstained = new(false, ReasonCode.None, "", null);

    /// <summary>型別綁定：payload 必須宣告自己適用於這個 ReasonCode。</summary>
    public bool PayloadTypeMatches => Payload is null || Payload.ValidFor.Contains(Code);
}

public sealed record AssuranceReport(
    CapabilityLayer? Capability,     // 拒答時為 null：不輸出任何信心相關數值
    CoverageLayer?   Coverage,
    StabilityLayer?  Stability,
    AbstentionLayer  Abstention,
    bool LikelihoodSensitive = false,
    SaturationEstimate? HypothesisCoverage = null)
{
    /// <summary>UI 用的保守等第：取三層最弱者。內部永遠保留三層，這只是顯示。</summary>
    public string ConservativeGrade()
    {
        if (Abstention.Abstained) return $"ABSTAIN({Abstention.Code})";
        var parts = new List<string>();
        if (Capability is { } c) parts.Add($"cap<={c.Cap:F2}{(c.Degraded ? "(degraded)" : "")}");
        if (Coverage  is { } v) parts.Add($"cover={v.TargetCoverage:P0}");
        if (Stability is { } s) parts.Add($"robust={s.Robustness:P0}");
        if (LikelihoodSensitive) parts.Add("likelihood-sensitive");
        return parts.Count == 0 ? "NO-ASSURANCE" : string.Join(" | ", parts);
    }
}

/// <summary>Chao1 假設覆蓋率（Phase 2 才會被填）。</summary>
public sealed record SaturationEstimate(int Singletons, int Doubletons, double EstimatedUndiscovered);
