// ============================================================================
//  CaseEvent — append-only 事件。DecisionCase 是這串事件的投影。
//  每個事件都帶 ActorId / ActorRole：權限矩陣在寫入邊界檢查，不在 prompt 裡約定。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Journal;

/// <summary>ActorRole 常數。LLM 角色（solver / critic / experiment_designer）永遠不能寫入確定性區的事件。</summary>
public static class Actors
{
    public const string System   = "system";      // orchestrator / workflow engine / 確定性引擎
    public const string Router   = "router";
    public const string Solver   = "solver";
    public const string Critic   = "critic";
    public const string ExperimentDesigner = "experiment_designer";
    public const string Analyst  = "analyst";
    public const string StageSolver = "stage_solver";    // 流水線段落：只能寫 Candidate 交付物
    public const string Machine  = "machine_verifier";   // L2 / L3 / L4
    public const string Human    = "human";
    public const string Evaluation = "evaluation";

    public static bool IsLlmRole(string role) => role is Solver or Critic or ExperimentDesigner or Analyst or StageSolver;
}

public abstract record CaseEvent
{
    public string CaseId { get; init; } = "";
    public int Seq { get; init; }
    public DateTime At { get; init; }
    public string StepId { get; init; } = "";
    public string ActorId { get; init; } = "";
    public string ActorRole { get; init; } = "";     // 空白 → Journal 拒絕（fail-closed）

    /// <summary>標記來源。用 with 而不是 required，讓引擎與 handler 可以先建事件再蓋章。</summary>
    public CaseEvent By(string stepId, string actorId, string actorRole) => this with { StepId = stepId, ActorId = actorId, ActorRole = actorRole };
}

// ── Intake ──
public sealed record CaseOpened(DecisionRequest Request, long PolicyVersion) : CaseEvent;
public sealed record EvidenceAdmitted(Evidence Evidence) : CaseEvent;
public sealed record UtilityMatrixSet(ImmutableArray<ActionOption> Actions, RiskPolicy Policy) : CaseEvent;   // 只有 Human 能寫

// ── Routing ──
public sealed record TriageDecided(bool Proceed, ReasonCode Code, string Explanation, string? Alternative, ProblemFacts Facts) : CaseEvent;
public sealed record PlanSet(WorkflowPlan Plan, ImmutableArray<string> StepIds) : CaseEvent;

// ── Execution ──
public sealed record AgentRunRecorded(AgentRun Run) : CaseEvent;
public sealed record ClaimProposed(string ClaimId, ClaimKind Kind, string Statement, double StatedConfidence,
                                   ImmutableArray<string> EvidenceFor, ImmutableArray<string> EvidenceAgainst) : CaseEvent;
public sealed record ExperimentPreRegistered(Experiment Experiment) : CaseEvent;
public sealed record ExperimentObserved(string ExperimentId, int OutcomeIndex) : CaseEvent;
public sealed record VerificationRecorded(VerificationResult Result) : CaseEvent;
public sealed record BeliefUpdated(ImmutableDictionary<string, double> Posterior, string Cause) : CaseEvent;
public sealed record DecisionMade(DecisionResult Result) : CaseEvent;
public sealed record HumanActed(string HumanRole, string What, bool Approved, string Rationale) : CaseEvent;

// ── Workflow 控制 ──
public sealed record StepCompleted(string Status) : CaseEvent;                    // ok / ok(retry n) / partial / failed
public sealed record PartialResult(string Note) : CaseEvent;                      // timeout 降級：必須降級能力層
public sealed record Noted(string Text) : CaseEvent;                              // 人類可讀的紀錄
public sealed record Halted(string Reason, ImmutableArray<string> SkippedSteps) : CaseEvent;

// ── 輸出與回填 ──
public sealed record Abstained(ReasonCode Code, string Explanation, string? Alternative) : CaseEvent;
public sealed record AssuranceIssued(AssuranceReport Report) : CaseEvent;
public sealed record OutcomeRecorded(Outcome Outcome) : CaseEvent;
