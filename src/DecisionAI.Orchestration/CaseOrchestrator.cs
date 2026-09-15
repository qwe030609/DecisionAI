// ============================================================================
//  CaseOrchestrator — 一個 case 的完整生命週期：
//  ① Intake（pin policy）→ ② Triage → ④ Strategy → ⑤ Plan → ⑥ Execute → ⑦ Assurance → ⑧ Outcome
//  （③ Tool/Model Router 在 Phase 2）
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Routing;
using DecisionAI.Modules.Verification;
using DecisionAI.Modules.Workflow;

namespace DecisionAI.Orchestration;

public sealed record CaseRun(ICaseJournal Journal, AssuranceReport Report)
{
    public CaseState State => Journal.State;
}

public sealed class CaseOrchestrator
{
    private readonly IPolicyStore _policy;
    private readonly IRolePermission _permission;
    private readonly IClock _clock;
    private readonly IEvidenceStore _evidence;
    private readonly VerifierRegistry _verifiers;
    private readonly IVerifiabilityTriage _triage;
    private readonly IStrategyRouter _strategy;
    private readonly IWorkflowEngine _engine;
    private readonly IAssuranceService _assurance;
    private readonly IEvaluationService _evaluation;
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _catalog;

    public CaseOrchestrator(IPolicyStore policy, IRolePermission permission, IClock clock, IEvidenceStore evidence,
                            VerifierRegistry verifiers, IVerifiabilityTriage triage, IStrategyRouter strategy,
                            IWorkflowEngine engine, IAssuranceService assurance, IEvaluationService evaluation,
                            IReadOnlyDictionary<string, WorkflowDefinition> catalog)
    {
        _policy = policy; _permission = permission; _clock = clock; _evidence = evidence; _verifiers = verifiers;
        _triage = triage; _strategy = strategy; _engine = engine; _assurance = assurance; _evaluation = evaluation; _catalog = catalog;
    }

    public async Task<CaseRun> RunAsync(string caseId, DecisionRequest req, IReadOnlyList<EvidenceDraft> evidence,
                                        long? pinPolicyVersion = null, CancellationToken ct = default)
    {
        // ① Intake：pin 一份 policy，全程只讀它
        var policy = _policy.Pin(pinPolicyVersion);
        var j = new CaseJournal(caseId, _permission, _clock);
        j.ApplyOrThrow(new CaseOpened(req, policy.Version).By("intake", "orchestrator", Actors.System));
        foreach (var d in evidence)
            j.ApplyOrThrow(new EvidenceAdmitted(_evidence.Admit(d)).By("intake", "orchestrator", Actors.System));
        if (req.ActionsOrEmpty.Length > 0)   // 效用矩陣：以 UtilityOwner（人）的身分寫入
            j.ApplyOrThrow(new UtilityMatrixSet(req.ActionsOrEmpty, req.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity))
                           .By("intake", "utility_owner", Actors.Human));

        // ② Triage：花任何 LLM token 之前決定該不該答
        var t = _triage.Assess(req, _verifiers.Inventory());
        j.ApplyOrThrow(new TriageDecided(t.Proceed, t.Code, t.Explanation, t.Alternative, t.Facts).By("triage", "triage", Actors.Router));
        if (!t.Proceed)
        {
            j.ApplyOrThrow(new Abstained(t.Code, t.Explanation, t.Alternative).By("triage", "triage", Actors.Router));
            return Finish(j);
        }

        // ④⑤ Strategy → Plan
        var plan = _strategy.Plan(t.Facts, policy);
        var wf = plan.Strategy == Strategy.MultiDomainPipeline ? WorkflowCatalog.BuildPipeline(req.StagesOrEmpty) : _catalog[plan.WorkflowName];
        j.ApplyOrThrow(new PlanSet(plan, wf.StepIds).By("plan", "router", Actors.Router));
        j.ApplyOrThrow(new Noted($"策略={plan.Strategy} workflow={plan.WorkflowName} solvers={plan.SolverCount} 人類核准={plan.RequireHumanApproval}").By("plan", "router", Actors.Router));
        foreach (var w in plan.Rationale) j.ApplyOrThrow(new Noted("  · " + w).By("plan", "router", Actors.Router));

        // ⑥ Execute（fail-closed：整個 DAG 被拒絕 → 一步都不跑）
        try { await _engine.RunAsync(wf, j, ct); }
        catch (WorkflowRejectedException ex)
        { j.ApplyOrThrow(new Halted(ex.Message, wf.StepIds).By("wf", "workflow", Actors.System)); }

        return Finish(j);
    }

    private CaseRun Finish(CaseJournal j)
    {
        // ⑦ Assurance：三層 + 拒絕判定
        var report = _assurance.Build(j.State);
        j.ApplyOrThrow(new AssuranceIssued(report).By("assurance", "assurance", Actors.System));
        return new CaseRun(j, report);
    }

    /// <summary>⑧ 真實結果回填 → Evaluation 產生 delta → Commit 新版本。進行中的 case 不受影響。</summary>
    public PolicySnapshot RecordOutcome(ICaseJournal j, Outcome outcome)
    {
        j.ApplyOrThrow(new OutcomeRecorded(outcome).By("outcome", "evaluation", Actors.Evaluation));
        var delta = _evaluation.Ingest(outcome, j.State);
        foreach (var n in delta.Notes) j.ApplyOrThrow(new Noted(n).By("outcome", "evaluation", Actors.Evaluation));
        return _policy.Commit(delta);
    }
}
