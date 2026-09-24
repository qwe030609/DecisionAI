// ============================================================================
//  CaseOrchestrator — 一個 case 的完整生命週期（Rev2 §5）：
//  ① Intake（pin policy + catalog 版本）
//  ② Triage       → 拒答 → 型別化 payload → 結束
//  ③ Tool/Model   → 轉介 → DelegationHandoff → 結束
//  ④ RoleAssign   → 先清點獨立性（策略才知道自己有幾雙獨立的眼睛）
//  ⑤ Strategy → ⑥ Execute → ⑦ Assurance → ⑧ Outcome
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
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
    private readonly IClaimCatalog _catalog;
    private readonly IRolePermission _permission;
    private readonly IClock _clock;
    private readonly IEvidenceStore _evidence;
    private readonly VerifierRegistry _verifiers;
    private readonly IVerifiabilityTriage _triage;
    private readonly IToolModelRouter _tools;
    private readonly IRoleAssigner _roles;
    private readonly IStrategyRouter _strategy;
    private readonly IWorkflowEngine _engine;
    private readonly IAssuranceService _assurance;
    private readonly IEvaluationService _evaluation;
    private readonly CatalogWriter _catalogWriter;
    private readonly PayloadBuilder _payloads;
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _workflows;
    private readonly IRandomSource _rng;
    private readonly IExperimentCatalog _experiments;
    private readonly IConformalSampleStore _conformal;

    public CaseOrchestrator(IPolicyStore policy, IClaimCatalog catalog, IRolePermission permission, IClock clock,
                            IEvidenceStore evidence, VerifierRegistry verifiers, IVerifiabilityTriage triage,
                            IToolModelRouter tools, IRoleAssigner roles, IStrategyRouter strategy, IWorkflowEngine engine,
                            IAssuranceService assurance, IEvaluationService evaluation, CatalogWriter catalogWriter,
                            PayloadBuilder payloads, IReadOnlyDictionary<string, WorkflowDefinition> workflows,
                            IRandomSource rng, IExperimentCatalog? experiments = null,
                            IConformalSampleStore? conformal = null)
    {
        _policy = policy; _catalog = catalog; _permission = permission; _clock = clock; _evidence = evidence;
        _verifiers = verifiers; _triage = triage; _tools = tools; _roles = roles; _strategy = strategy;
        _engine = engine; _assurance = assurance; _evaluation = evaluation; _catalogWriter = catalogWriter;
        _payloads = payloads; _workflows = workflows; _rng = rng;
        _experiments = experiments ?? new InMemoryExperimentCatalog();
        _conformal = conformal ?? new InMemoryConformalSampleStore();
    }

    public async Task<CaseRun> RunAsync(string caseId, DecisionRequest req, IReadOnlyList<EvidenceDraft> evidence,
                                        long? pinPolicyVersion = null, CancellationToken ct = default)
    {
        // ① Intake：pin 一份 policy 與 catalog 版本，全程只讀它
        var policy = _policy.Pin(pinPolicyVersion);
        long catalogVersion = _catalog.CurrentVersion;
        var j = new CaseJournal(caseId, _permission, _clock);
        j.ApplyOrThrow(new CaseOpened(req, policy.Version, catalogVersion).By("intake", "orchestrator", Actors.System));
        foreach (var d in evidence)
            j.ApplyOrThrow(new EvidenceAdmitted(_evidence.Admit(d)).By("intake", "orchestrator", Actors.System));
        if (req.ActionsOrEmpty.Length > 0)   // 效用矩陣：以 UtilityOwner（人）的身分寫入
            j.ApplyOrThrow(new UtilityMatrixSet(req.ActionsOrEmpty, req.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity))
                           .By("intake", "utility_owner", Actors.Human));

        // ② Triage：花任何 LLM token 之前決定該不該答
        var t = _triage.Assess(req, _verifiers.Inventory());
        j.ApplyOrThrow(new TriageDecided(t.Proceed, t.Code, t.Explanation, t.Facts).By("triage", "triage", Actors.Router));
        if (!t.Proceed) return await AbstainAsync(j, t.Code, t.Explanation, ct);

        // ③ Tool/Model Router：屬專業數值模型領域 → 轉介，LLM 只翻譯與解釋
        var d2 = _tools.Decide(req);
        j.ApplyOrThrow(new DelegationDecided(d2.Delegate, d2.Specialist?.ToolId, d2.Reason).By("delegate", "tool-router", Actors.Router));
        if (d2.Delegate)
            return await AbstainAsync(j, ReasonCode.DelegatedToExternalModel,
                $"轉介 {d2.Specialist!.ToolId}：{d2.Reason}", ct);

        // ④ RoleAssign：先清點可用的獨立性——可用的獨立性決定了哪些策略是誠實的
        var survey = _roles.Survey(t.Facts.Domain);
        foreach (var w in survey.Rationale) j.ApplyOrThrow(new Noted(w).By("roles", "assigner", Actors.System));

        // ⑤ Strategy
        var plan = _strategy.Plan(t.Facts, policy, survey.Budget, survey.Degradation);

        // ④b 依計畫需要的角色做實際指派（硬約束：critic ∉ 本案 solver）
        //     亂數子流以 caseId 命名：同一個 case 重跑必然抽到同一組角色（MR-10 可重放），
        //     不同 case 之間則各自獨立，Thompson 的探索才不會被鎖死在同一個順序上。
        var demand = BuildDemand(plan, t.Facts.Domain);
        var assignment = _roles.Assign(demand, policy, _rng.Fork(caseId));
        j.ApplyOrThrow(new RolesAssigned(assignment.Slots, assignment.Budget, assignment.Degradation, assignment.Rationale)
                       .By("roles", "assigner", Actors.System));

        var wf = plan.Strategy == Strategy.MultiDomainPipeline ? WorkflowCatalog.BuildPipeline(req.StagesOrEmpty) : _workflows[plan.WorkflowName];
        j.ApplyOrThrow(new PlanSet(plan, wf.StepIds).By("plan", "router", Actors.Router));
        j.ApplyOrThrow(new Noted($"策略={plan.Strategy} workflow={plan.WorkflowName} solvers={plan.SolverCount} critic={(plan.IncludeCritic ? "有" : "無")} 人類核准={plan.RequireHumanApproval}").By("plan", "router", Actors.Router));
        foreach (var w in plan.Rationale) j.ApplyOrThrow(new Noted("  · " + w).By("plan", "router", Actors.Router));

        // ⑥ Execute（fail-closed：整個 DAG 被拒絕 → 一步都不跑）
        try { await _engine.RunAsync(wf, j, ct); }
        catch (WorkflowRejectedException ex)
        { j.ApplyOrThrow(new Halted(ex.Message, wf.StepIds).By("wf", "workflow", Actors.System)); }

        return await FinishAsync(j, ct);
    }

    /// <summary>角色需求由計畫決定；stage_solver 走 solver 池。</summary>
    private static RoleDemand BuildDemand(WorkflowPlan plan, string domain)
    {
        var needs = ImmutableArray.CreateBuilder<(string, int)>();
        needs.Add(("solver", plan.Strategy == Strategy.MultiDomainPipeline ? 1 : plan.SolverCount));
        if (plan.IncludeCritic) needs.Add(("critic", 1));
        if (plan.Strategy is Strategy.SolverCriticVerifier) needs.Add(("experiment_designer", 1));
        return new RoleDemand(needs.ToImmutable(), domain);
    }

    private async Task<CaseRun> AbstainAsync(CaseJournal j, ReasonCode code, string explanation, CancellationToken ct)
    {
        var outcome = await _payloads.BuildAsync(j.State, code, ct);
        foreach (var e in outcome.Events) j.ApplyOrThrow(e);
        j.ApplyOrThrow(new Abstained(code, explanation, outcome.Payload).By("triage", "abstention", Actors.Router));
        return Finish(j);
    }

    private async Task<CaseRun> FinishAsync(CaseJournal j, CancellationToken ct)
    {
        // 流程中止 / 無可採行動時，Assurance 會補上確定性的 BlockedHandback，不需要再生成
        var report = _assurance.Build(j.State);
        if (report.Abstention.Abstained && j.State.Abstention is null)
            j.ApplyOrThrow(new Abstained(report.Abstention.Code, report.Abstention.Explanation, report.Abstention.Payload)
                           .By("assurance", "abstention", Actors.Router));
        await Task.CompletedTask;
        return Finish(j);
    }

    private CaseRun Finish(CaseJournal j)
    {
        // ⑦ Assurance：三層 + 拒絕判定
        var report = _assurance.Build(j.State);
        j.ApplyOrThrow(new AssuranceIssued(report).By("assurance", "assurance", Actors.System));
        return new CaseRun(j, report);
    }

    /// <summary>
    /// ⑧ 真實結果回填 → Evaluation → PolicyDelta + CatalogDelta → Commit。進行中的 case 不受影響。
    ///
    /// Phase 3 在這裡多做三件跨案的事，都是「數出來」而不是「估出來」的：
    ///   · 實驗歷史頻率進 ExperimentCatalog（下次同一個實驗就不必再靠定性估計）
    ///   · conformal 樣本進樣本庫，並重建分位數（校準集算門檻，稽核集量覆蓋率）
    ///   · 兩者的版本號一併寫進 PolicySnapshot，讓下一個 case pin 得到
    /// </summary>
    public PolicySnapshot RecordOutcome(ICaseJournal j, Outcome outcome)
    {
        j.ApplyOrThrow(new OutcomeRecorded(outcome).By("outcome", "evaluation", Actors.Evaluation));
        var (delta, harvest) = _evaluation.Ingest(outcome, j.State);
        foreach (var n in delta.Notes) j.ApplyOrThrow(new Noted(n).By("outcome", "evaluation", Actors.Evaluation));
        foreach (var c in delta.CatalogEntries)
            j.ApplyOrThrow(new CatalogEntryProposed(c.DraftId, c.Frame, c.Domain).By("outcome", "evaluation", Actors.Evaluation));
        _catalogWriter.Commit(delta);

        foreach (var e in harvest.ExperimentObservations)
            _experiments.Record(e.ExperimentKey, e.ClaimCatalogId, e.OutcomeIndex, e.OutcomeCount);
        foreach (var c in harvest.ConformalSamples) _conformal.Add(c);

        var quantiles = _conformal.RebuildAll();
        foreach (var q in quantiles.OrderBy(k => k.Key, StringComparer.Ordinal))
            j.ApplyOrThrow(new Noted(
                $"conformal（{q.Key}）：校準 {q.Value.CalibrationSize} 筆 → 門檻 {q.Value.Threshold:F3}；" +
                $"稽核 {q.Value.AuditSize} 筆實測覆蓋率 " +
                $"{(q.Value.AuditSize == 0 ? "尚無" : _conformal.EmpiricalCoverage(q.Key, q.Value).ToString("P0"))}" +
                $"（目標 {1 - q.Value.Alpha:P0}）").By("outcome", "evaluation", Actors.Evaluation));

        return _policy.Commit(delta with
        {
            CatalogVersionAfter = _catalog.CurrentVersion,
            ConformalUpdates = quantiles,
            ExperimentCatalogVersionAfter = _experiments.CurrentVersion
        });
    }
}
