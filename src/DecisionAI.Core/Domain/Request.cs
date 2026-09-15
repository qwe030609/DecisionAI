// ============================================================================
//  請求與規劃 — ProblemFacts 只放事實與類別，不放 LLM 自估的小數。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public enum RiskLevel { Low, Medium, High }

/// <summary>Triage 第二問：各方有沒有同意的 ground-truth 定義？由請求者宣告（事實），不是 LLM 猜。</summary>
public enum GroundTruthStatus
{
    Agreed,       // 有客觀可檢核的答案
    Contested,    // 「答案是什麼」本身有爭議 → 定義爭議
    Undefined     // 根本沒有 ground truth（價值觀、品味）
}

public sealed record Constraints(double Budget, int MaxLatencySeconds, RiskLevel RiskLevel);

/// <summary>行動選項：效用矩陣是「人 / 政策表」給的確定性輸入；LLM 可以提案文字，不能填數字。</summary>
public sealed record ActionOption(string Id, string Description, ImmutableDictionary<string, double> UtilityByScenario)
{
    public double Utility(string scenario) => UtilityByScenario.GetValueOrDefault(scenario, 0);
}

public sealed record RiskPolicy(double MaxAcceptableLoss, double RobustnessDelta = 0.10, int RobustnessSamples = 500);

public sealed record StageSpec(string Name, string Task, VerifierLevel Verifier, string Contract);

public sealed record DecisionRequest(
    string Problem,
    string Goal,
    string Domain,
    Constraints Constraints,
    GroundTruthStatus GroundTruth = GroundTruthStatus.Agreed,
    bool Reflexive = false,               // 預測會改變系統 → 禁止方向預測
    bool Probabilistic = false,
    ImmutableArray<StageSpec> Stages = default,
    ImmutableArray<ActionOption> Actions = default,   // 由 UtilityOwner（人）提供；進 Journal 時以 Human 角色寫入
    RiskPolicy? RiskPolicy = null)
{
    public ImmutableArray<StageSpec> StagesOrEmpty  => Stages.IsDefault  ? ImmutableArray<StageSpec>.Empty    : Stages;
    public ImmutableArray<ActionOption> ActionsOrEmpty => Actions.IsDefault ? ImmutableArray<ActionOption>.Empty : Actions;
}

public enum Strategy { SingleAgent, SolverCriticVerifier, MultiDomainPipeline }

/// <summary>ProblemFacts 是「事實 + 類別」。驗證器等級是附了什麼，不是估計出來的。</summary>
public sealed record ProblemFacts(
    VerifierLevel BestAvailableVerifier,
    bool Reflexive,
    bool Probabilistic,
    bool HasUtilities,
    int StageCount,
    string Domain,
    RiskLevel Risk,
    GroundTruthStatus GroundTruth);

public sealed record WorkflowPlan(
    Strategy Strategy,
    string WorkflowName,
    int SolverCount,
    bool RequireHumanApproval,
    ImmutableArray<string> Rationale);
