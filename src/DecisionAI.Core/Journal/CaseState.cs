// ============================================================================
//  CaseState — 事件流的不可變投影。Fold(events) 是純函數；
//  Journal 只是把 Reduce 逐步套用，測試可以斷言 Fold(Events) == State。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;

namespace DecisionAI.Core.Journal;

public sealed record CaseState
{
    public string Id { get; init; } = "";
    public DecisionRequest? Request { get; init; }
    public long PolicyVersion { get; init; }
    public ProblemFacts? Facts { get; init; }
    public TriageDecided? Triage { get; init; }
    public WorkflowPlan? Plan { get; init; }
    public ImmutableArray<string> PlannedSteps { get; init; } = ImmutableArray<string>.Empty;

    public ImmutableList<Evidence> Evidence { get; init; } = ImmutableList<Evidence>.Empty;
    public ImmutableList<Claim> Claims { get; init; } = ImmutableList<Claim>.Empty;
    public ImmutableList<Experiment> Experiments { get; init; } = ImmutableList<Experiment>.Empty;
    public ImmutableList<AgentRun> AgentRuns { get; init; } = ImmutableList<AgentRun>.Empty;
    public ImmutableList<VerificationResult> Verifications { get; init; } = ImmutableList<VerificationResult>.Empty;
    public ImmutableDictionary<string, double> Beliefs { get; init; } = ImmutableDictionary<string, double>.Empty;
    public ImmutableArray<ActionOption> Utilities { get; init; } = ImmutableArray<ActionOption>.Empty;
    public RiskPolicy? RiskPolicy { get; init; }
    public DecisionResult? Decision { get; init; }
    public AssuranceReport? Assurance { get; init; }
    public Outcome? Outcome { get; init; }

    public double CostSpent { get; init; }
    public ImmutableDictionary<string, string> StepStatus { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableHashSet<string> PartialSteps { get; init; } = ImmutableHashSet<string>.Empty;
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;
    public bool Halted { get; init; }
    public string? HaltReason { get; init; }
    public ImmutableArray<string> SkippedSteps { get; init; } = ImmutableArray<string>.Empty;
    public AbstentionLayer? Abstention { get; init; }

    // ── 衍生查詢 ──
    public IEnumerable<Claim> Hypotheses => Claims.Where(c => c.Kind == ClaimKind.Hypothesis && !c.IsOpinion);
    public IEnumerable<string> AgentsUsedAs(string role) => AgentRuns.Where(r => r.Role == role).Select(r => r.AgentId).Distinct();
    public VerifierLevel BestPassedLevel()
        => Verifications.Where(v => v.Pass == true).Select(v => v.Level).DefaultIfEmpty(VerifierLevel.None).Max();
    public string Domain => Facts?.Domain ?? Request?.Domain ?? "general";

    public static CaseState Empty(string id) => new() { Id = id };
}

public static class CaseReducer
{
    public static CaseState Fold(string caseId, IEnumerable<CaseEvent> events) => events.Aggregate(CaseState.Empty(caseId), Reduce);

    public static CaseState Reduce(CaseState s, CaseEvent e) => e switch
    {
        CaseOpened o           => s with { Request = o.Request, PolicyVersion = o.PolicyVersion },
        EvidenceAdmitted ev    => s with { Evidence = s.Evidence.Add(ev.Evidence) },
        UtilityMatrixSet u     => s with { Utilities = u.Actions, RiskPolicy = u.Policy },
        TriageDecided t        => s with { Triage = t, Facts = t.Facts, Abstention = t.Proceed ? s.Abstention : new AbstentionLayer(true, t.Code, t.Explanation, t.Alternative) },
        PlanSet p              => s with { Plan = p.Plan, PlannedSteps = p.StepIds },
        AgentRunRecorded r     => s with { AgentRuns = s.AgentRuns.Add(r.Run), CostSpent = s.CostSpent + r.Run.Cost },
        ClaimProposed c        => s with { Claims = UpsertClaim(s.Claims, c) },
        ExperimentPreRegistered x => s with { Experiments = s.Experiments.Add(x.Experiment) },
        ExperimentObserved ob  => s with { Experiments = s.Experiments.Select(x => x.Id == ob.ExperimentId ? x with { ObservedOutcome = ob.OutcomeIndex } : x).ToImmutableList() },
        VerificationRecorded v => ApplyVerification(s, v.Result),
        BeliefUpdated b        => s with { Beliefs = b.Posterior, Log = s.Log.Add($"[{e.StepId}] {b.Cause}") },
        DecisionMade d         => s with { Decision = d.Result },
        HumanActed h           => s with { Log = s.Log.Add($"[{e.StepId}] 人類（{h.HumanRole}）「{h.What}」→ {(h.Approved ? "同意" : "拒絕")}：{h.Rationale}") },
        StepCompleted sc       => s with { StepStatus = s.StepStatus.SetItem(e.StepId, sc.Status) },
        PartialResult pr       => s with { PartialSteps = s.PartialSteps.Add(e.StepId), Log = s.Log.Add($"[{e.StepId}] 部分結果：{pr.Note}") },
        Noted n                => s with { Log = s.Log.Add($"[{e.StepId}] {n.Text}") },
        Halted h               => s with { Halted = true, HaltReason = h.Reason, SkippedSteps = h.SkippedSteps, Log = s.Log.Add($"[halt] {h.Reason}") },
        Abstained a            => s with { Abstention = new AbstentionLayer(true, a.Code, a.Explanation, a.Alternative) },
        AssuranceIssued ai     => s with { Assurance = ai.Report },
        OutcomeRecorded oc     => s with { Outcome = oc.Outcome },
        _ => throw new NotSupportedException($"未知事件型別 {e.GetType().Name}")   // fail-closed
    };

    private static ImmutableList<Claim> UpsertClaim(ImmutableList<Claim> claims, ClaimProposed c)
    {
        var idx = claims.FindIndex(k => k.Id == c.ClaimId);
        var proposal = new Proposal(c.ActorId, c.StatedConfidence);
        if (idx < 0)
            return claims.Add(new Claim(c.ClaimId, c.Kind, c.Statement, ImmutableList.Create(proposal),
                                        c.EvidenceFor.ToImmutableHashSet(), c.EvidenceAgainst.ToImmutableHashSet(), false));
        var k = claims[idx];
        return claims.SetItem(idx, k with
        {
            Proposals = k.Proposals.Add(proposal),
            EvidenceFor = k.EvidenceFor.Union(c.EvidenceFor),
            EvidenceAgainst = k.EvidenceAgainst.Union(c.EvidenceAgainst)
        });
    }

    private static CaseState ApplyVerification(CaseState s, VerificationResult r)
    {
        var claims = s.Claims;
        // 被取消資格（引用不存在 / 沒有支持證據）→ 該主張是意見，不進機率引擎
        if (r.Disqualifies && r.ClaimId is not null)
        {
            var idx = claims.FindIndex(k => k.Id == r.ClaimId);
            if (idx >= 0) claims = claims.SetItem(idx, claims[idx] with { IsOpinion = true });
        }
        return s with { Verifications = s.Verifications.Add(r), Claims = claims };
    }
}
