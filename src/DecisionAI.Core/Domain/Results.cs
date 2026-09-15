// ============================================================================
//  Deterministic Zone 的產物：全部是不可變 record。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public sealed record AgentRun(string AgentId, string Role, string RawOutput, double Cost, TimeSpan Elapsed, bool Ok, string? Error);

/// <summary>Disqualifies = 這條結果把主張踢出機率引擎（例如引用不存在、沒有任何支持證據）；一般的「未過」只是分數低。</summary>
public sealed record VerificationResult(string VerifierId, VerifierLevel Level, string? ClaimId, bool? Pass, double Score, string Note, bool Disqualifies = false);

public sealed record ActionMetrics(double ExpectedUtility, double WorstCase, double MaxRegret, bool Admissible);

public sealed record DecisionResult(
    string? RecommendedAction,
    double ExpectedUtility,
    double WorstCase,
    double MaxRegret,
    double Robustness,
    string Reason,
    ImmutableDictionary<string, ActionMetrics> PerAction);

public sealed record Outcome(string? TrueClaimId, ImmutableDictionary<string, bool> ClaimTruth, VerifierLevel Level, string Note);

/// <summary>Agent 譜系欄位是 Phase 3 相關性估計的基礎；Phase 1 只用來做「同廠商 / 同家族」的選擇懲罰。</summary>
public sealed record AgentSpec(
    string AgentId,
    string Vendor,
    string BaseModelFamily,
    string TrainingGeneration,
    ImmutableArray<string> Roles,
    ImmutableArray<string> Domains,        // "*" = 通用
    CostProfile Cost);

public sealed record CostProfile(double InPer1k, double OutPer1k);
