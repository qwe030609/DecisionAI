// ============================================================================
//  Deterministic Zone 的產物：全部是不可變 record。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public sealed record AgentRun(string AgentId, string Role, string RawOutput, double Cost, TimeSpan Elapsed, bool Ok, string? Error);

/// <summary>Disqualifies = 這條結果把主張踢出機率引擎（引用不存在、沒有任何支持證據）；一般的「未過」只是分數低。</summary>
public sealed record VerificationResult(string VerifierId, VerifierLevel Level, string? ClaimId, bool? Pass,
                                        double Score, string Note, bool Disqualifies = false);

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

/// <summary>
/// Agent 譜系欄位是相關性估計的基礎；Rev2 用 BaseModelFamily 計算 IndependenceBudget。
/// EligibleRoles 是「有資格擔任」，實際指派由 RoleAssigner 決定（Phase 2 加探針資格門檻）。
/// </summary>
public sealed record AgentSpec(
    string AgentId,
    string Vendor,
    string BaseModelFamily,
    string TrainingGeneration,
    ImmutableArray<string> EligibleRoles,
    ImmutableArray<string> Domains,        // "*" = 通用
    CostProfile Cost);

public sealed record CostProfile(double InPer1k, double OutPer1k);
