// ============================================================================
//  Benchmark Runner — 把 32 個案例真的跑過 CaseOrchestrator，輸出 JSON 報告。
//  dotnet run --project tools/DecisionAI.Benchmark -- [輸出路徑]
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DecisionAI.Benchmark;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Testing;

Console.OutputEncoding = Encoding.UTF8;
string outPath = args.Length > 0 ? args[0] : "benchmark-results.json";

var rows = new List<object>();
int passed = 0, abstainedCount = 0;

TestSystemOptions BaseOptions(BenchCase c) => new()
{
    IncludeL3Executable = c.HasL3,
    IncludeL4Experiment = c.HasL4,
    Agents = () => ScriptedWorld.Agents(c),
    L3Test = ScriptedWorld.TestVerifier(c),
    L4Run = ScriptedWorld.ExperimentRunner(c)
};

DecisionRequest Request(BenchCase c) => new(
    Problem: c.Prompt, Goal: c.Title, Domain: c.Domain,
    Constraints: new Constraints(c.Budget, 120, c.Risk),
    GroundTruth: c.GroundTruth, Reflexive: c.Reflexive,
    Stages: c.Stages, Actions: c.Actions,
    RiskPolicy: c.Actions.IsEmpty ? null : new RiskPolicy(MaxAcceptableLoss: 50));

foreach (var c in BenchmarkCases.All)
{
    var sys = TestSystem.Build(BaseOptions(c));

    var run = await sys.Orchestrator.RunAsync($"BM-{c.Id}", Request(c), c.Evidence);
    var s = run.State;
    var checks = Checks.RunAll(c, run, sys);
    bool pass = checks.All(x => x.Pass);
    if (pass) passed++;
    if (run.Report.Abstention.Abstained) abstainedCount++;

    // 真實結果回填（僅在有假設且有實驗/測試時）→ 展示 PolicySnapshot 推進
    long policyAfter = s.PolicyVersion;
    var evalNotes = ImmutableArray<string>.Empty;
    if (!run.Report.Abstention.Abstained && s.Claims.Any(k => k.Kind == ClaimKind.Hypothesis) && c.HasL3)
    {
        string trueId = $"H{c.TrueHypothesis}";
        var truth = s.Claims.ToImmutableDictionary(k => k.Id, k => k.Id == trueId);
        var next = sys.Orchestrator.RecordOutcome(run.Journal, new Outcome(trueId, truth,
            VerifierLevel.L5_RealOutcome, $"部署後實測：真相為 {trueId}"));
        policyAfter = next.Version;
        evalNotes = s.Log.Where(l => l.StartsWith("[outcome]")).Select(l => l["[outcome] ".Length..]).ToImmutableArray();
    }

    var forbidden = Checks.ForbiddenAudit(c, run, GptForbidden.For(c.Id));

    rows.Add(new
    {
        id = c.Id, dimension = c.Dimension, level = c.Level, title = c.Title, prompt = c.Prompt, domain = c.Domain,
        facts = new
        {
            groundTruth = c.GroundTruth.ToString(), reflexive = c.Reflexive, risk = c.Risk.ToString(),
            attachedVerifiers = Attached(c), stageCount = Math.Max(1, c.Stages.Length),
            hasUtilities = !c.Actions.IsEmpty, evidenceCount = c.Evidence.Length,
            evidenceSources = c.Evidence.Select(e => e.Source.ToString()).Distinct().ToArray()
        },
        expected = new
        {
            disposition = c.Expected.Disposition.ToString(), code = c.Expected.Code.ToString(),
            strategy = c.Expected.Strategy?.ToString(), workflow = c.Expected.Workflow
        },
        why = c.WhyRev1,
        result = new
        {
            abstained = run.Report.Abstention.Abstained,
            reasonCode = run.Report.Abstention.Code.ToString(),
            explanation = run.Report.Abstention.Explanation,
            alternative = run.Report.Abstention.Alternative,
            strategy = s.Plan?.Strategy.ToString(), workflow = s.Plan?.WorkflowName,
            solverCount = s.Plan?.SolverCount ?? 0, requireHuman = s.Plan?.RequireHumanApproval ?? false,
            rationale = s.Plan?.Rationale.ToArray() ?? Array.Empty<string>(),
            grade = run.Report.ConservativeGrade(),
            capability = run.Report.Capability is { } cap ? new { level = cap.BestPassedLevel.ToString(), cap = cap.Cap, degraded = cap.Degraded } : null,
            coverage = (object?)null,
            stability = run.Report.Stability is { } st ? new { robustness = st.Robustness, worstCase = st.WorstCase, maxRegret = st.MaxRegret } : null,
            recommendedAction = s.Decision?.RecommendedAction,
            decisionReason = s.Decision?.Reason,
            perAction = s.Decision?.PerAction.OrderBy(k => k.Key).Select(k => new { id = k.Key, eu = k.Value.ExpectedUtility, worst = k.Value.WorstCase, regret = k.Value.MaxRegret, admissible = k.Value.Admissible }).ToArray(),
            beliefs = s.Beliefs.OrderByDescending(k => k.Value).Select(k => new { claim = k.Key, p = k.Value }).ToArray(),
            beliefsRendered = Checks.RenderBeliefs(s),
            claims = s.Claims.Select(k => new { id = k.Id, kind = k.Kind.ToString(), statement = k.Statement, isOpinion = k.IsOpinion, proposals = k.Proposals.Select(p => new { agent = p.AgentId, conf = p.StatedConfidence }).ToArray(), evidenceFor = k.EvidenceFor.OrderBy(x => x).ToArray() }).ToArray(),
            verifications = s.Verifications.Select(v => new { verifier = v.VerifierId, level = v.Level.ToString(), claim = v.ClaimId, pass = v.Pass, score = v.Score, note = v.Note, disqualifies = v.Disqualifies }).ToArray(),
            experiments = s.Experiments.Select(e => new { id = e.Id, description = e.Description, designedBy = e.DesignedBy, outcomes = e.Outcomes.ToArray(), observed = e.ObservedOutcome, likelihoods = e.LikelihoodByClaim.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { claim = k.Key, row = k.Value.ToArray() }).ToArray() }).ToArray(),
            steps = s.PlannedSteps.Select(id => new { id, status = s.StepStatus.GetValueOrDefault(id, "—") }).ToArray(),
            halted = s.Halted, haltReason = s.HaltReason, skipped = s.SkippedSteps.ToArray(),
            log = s.Log.Where(l => !l.StartsWith("[outcome]")).ToArray()
        },
        execution = new
        {
            llmCalls = sys.LlmCallCount, cost = Math.Round(s.CostSpent, 4), budget = c.Budget,
            events = run.Journal.Events.Count, policyBefore = 0, policyAfter,
            humanGates = run.Journal.Events.OfType<HumanActed>().Count(),
            eventKinds = run.Journal.Events.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()).Select(g => new { kind = g.Key, n = g.Count() }).ToArray(),
            evalNotes = evalNotes.ToArray()
        },
        checks = checks.Select(x => new { name = x.Name, pass = x.Pass, detail = x.Detail }).ToArray(),
        pass,
        forbidden = forbidden.Select(f => new { behavior = f.Behavior, status = f.Status, why = f.Why }).ToArray()
    });

    string mark = pass ? "PASS" : "FAIL";
    string disp = run.Report.Abstention.Abstained ? $"ABSTAIN {run.Report.Abstention.Code}" : $"{s.Plan!.Strategy}";
    Console.WriteLine($"{c.Id,-3} {mark}  {disp,-34} LLM {sys.LlmCallCount,2} 次  成本 {s.CostSpent,6:F3}  事件 {run.Journal.Events.Count,3}  {run.Report.ConservativeGrade()}");
}

// ══════════════════ Mutation testing：把守門件換成壞的，檢查必須變紅 ══════════════════
Console.WriteLine("\n── Mutation testing（壞的守門件必須被抓到）──");
var mutations = new List<object>();
foreach (var m in Mutations.All)
{
    var caught = new List<object>();
    int failed = 0, affected = 0, missed = 0;
    var caughtBy = new Dictionary<string, int>();

    foreach (var c in BenchmarkCases.All)
    {
        var sys = TestSystem.Build(m.Apply(BaseOptions(c)));
        ImmutableArray<CheckResult> checks;
        string? crash = null;
        try
        {
            var run = await sys.Orchestrator.RunAsync($"MUT-{m.Id}-{c.Id}", Request(c), c.Evidence);
            checks = Checks.RunAll(c, run, sys);
        }
        catch (Exception ex)
        {
            crash = ex.GetType().Name + ": " + ex.Message;
            checks = ImmutableArray.Create(new CheckResult("Engine", false, crash));
        }
        bool isAffected = m.Affected(c);
        if (isAffected) affected++;
        var bad = checks.Where(x => !x.Pass).Select(x => x.Name).ToArray();
        if (isAffected && bad.Length == 0) missed++;
        if (bad.Length > 0)
        {
            failed++;
            foreach (var n in bad) caughtBy[n] = caughtBy.GetValueOrDefault(n) + 1;
            caught.Add(new { id = c.Id, caughtBy = bad, detail = checks.First(x => !x.Pass).Detail });
        }
    }

    double rate = affected == 0 ? 1 : (affected - missed) / (double)affected;
    Console.WriteLine($"{m.Id} {m.Name,-34} 受影響 {affected,2} 案，抓到 {affected - missed,2} 案（{rate:P0}）  由 {string.Join(", ", caughtBy.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}"))}");
    mutations.Add(new
    {
        id = m.Id, name = m.Name, target = m.Target, affectedWhy = m.AffectedWhy,
        detected = failed, affected, missed, total = BenchmarkCases.All.Length, rate,
        caughtBy = caughtBy.OrderByDescending(k => k.Value).Select(k => new { check = k.Key, n = k.Value }).ToArray(),
        cases = caught
    });
}

var report = new
{
    benchmark = "DecisionAI Rev1 Phase 1 · Router Benchmark",
    version = "1.0",
    generatedAt = DateTime.UtcNow.ToString("O"),
    note = "LLM 為腳本化（ScriptedLlm），量的是控制流程、守門件與確定性引擎，不是模型品質。",
    summary = new { total = rows.Count, passed, abstained = abstainedCount, proceeded = rows.Count - abstainedCount },
    mutations,
    cases = rows
};

JsonSerializerOptions opts = new()
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};
File.WriteAllText(outPath, JsonSerializer.Serialize(report, opts));
Console.WriteLine($"\n{passed}/{rows.Count} 通過 → {Path.GetFullPath(outPath)}");

static string[] Attached(BenchCase c)
{
    var a = new List<string> { "L1_LlmCritic", "L2_Rule" };
    if (c.HasL3) a.Add("L3_ExecutableTest");
    if (c.HasL4) a.Add("L4_Experiment");
    return a.ToArray();
}
