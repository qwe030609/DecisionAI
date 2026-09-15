// ============================================================================
//  角色權限矩陣 — 在寫入邊界強制（D4）。預設 deny。
//  critic 寫不進 Claim、任何 LLM 角色寫不進效用矩陣 / 信念 / 決策。
// ============================================================================

using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;

namespace DecisionAI.Modules.Journal;

public sealed class RolePermissionMatrix : IRolePermission
{
    public bool CanWrite(string actorRole, CaseEvent e) => actorRole switch
    {
        Actors.System or Actors.Router => e is not (ClaimProposed or ExperimentPreRegistered or UtilityMatrixSet or HumanActed),

        Actors.Solver             => e is ClaimProposed { Kind: ClaimKind.Hypothesis or ClaimKind.Forecast },
        Actors.Analyst or Actors.StageSolver => e is ClaimProposed { Kind: ClaimKind.Candidate },
        Actors.Critic             => e is VerificationRecorded { Result.Level: VerifierLevel.L1_LlmCritic },
        Actors.ExperimentDesigner => e is ExperimentPreRegistered,

        Actors.Machine => e is VerificationRecorded { Result.Level: >= VerifierLevel.L2_Rule }
                            or ExperimentObserved
                            or EvidenceAdmitted { Evidence.Source: EvidenceSource.ExperimentResult }
                            or Noted,

        Actors.Human      => e is HumanActed or UtilityMatrixSet or EvidenceAdmitted { Evidence.Source: EvidenceSource.UserInput },
        Actors.Evaluation => e is OutcomeRecorded or Noted,
        _ => false
    };
}

/// <summary>Mutation testing 用：把守門件換成壞的，測試套件必須變紅。</summary>
public sealed class AlwaysAllowPermission : IRolePermission
{
    public bool CanWrite(string actorRole, CaseEvent e) => true;
}
