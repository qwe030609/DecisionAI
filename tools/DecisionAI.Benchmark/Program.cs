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
var baselines = new Dictionary<string, DecisionAI.Orchestration.CaseRun>();
int passed = 0, abstainedCount = 0;

TestSystemOptions BaseOptions(BenchCase c) => new()
{
    IncludeL3Executable = c.HasL3,
    IncludeL4Experiment = c.HasL4,
    Agents = () => ScriptedWorld.Agents(c),
    ClaimCatalog = ScriptedWorld.Catalog(c),
    L3Test = ScriptedWorld.TestVerifier(c),
    L4Run = ScriptedWorld.ExperimentRunner(c)
};

DecisionRequest Request(BenchCase c) => new(
    Problem: c.Prompt, Goal: c.Title, Domain: c.Domain,
    Constraints: new Constraints(c.Budget, 120, c.Risk),
    GroundTruth: c.GroundTruth, Reflexive: c.Reflexive, Specialist: c.Specialist,
    Stages: c.Stages, Actions: c.Actions,
    RiskPolicy: c.Actions.IsEmpty ? null : new RiskPolicy(MaxAcceptableLoss: 50));

foreach (var c in BenchmarkCases.All)
{
    var sys = TestSystem.Build(BaseOptions(c));
    var run = await sys.Orchestrator.RunAsync($"BM-{c.Id}", Request(c), c.Evidence);
    var s = run.State;
    var checks = Checks.RunAll(c, run, sys);
    baselines[c.Id] = run;                 // mutation 的可觀察性以 baseline 實際發生什麼為準
    bool pass = checks.All(x => x.Pass);
    if (pass) passed++;
    if (run.Report.Abstention.Abstained) abstainedCount++;

    long policyAfter = s.PolicyVersion;
    long catalogAfter = sys.ClaimCatalog.CurrentVersion;
    var evalNotes = ImmutableArray<string>.Empty;
    if (!run.Report.Abstention.Abstained && s.Claims.Any(k => k.Kind == ClaimKind.Hypothesis) && c.HasL3)
    {
        // 真相以「宣告清單裡的第幾條」表示；本地編號取決於指派順序，不能直接當真相
        var trueFrame = c.Hypotheses[c.TrueHypothesis - 1].Frame;
        string trueId = s.Claims.FirstOrDefault(k => k.Frame.Identity == trueFrame.Identity)?.LocalId ?? $"H{c.TrueHypothesis}";
        var truth = s.Claims.ToImmutableDictionary(k => k.LocalId, k => k.LocalId == trueId);
        var next = sys.Orchestrator.RecordOutcome(run.Journal, new Outcome(trueId, truth,
            VerifierLevel.L5_RealOutcome, $"部署後實測：真相為 {trueId}"));
        policyAfter = next.Version;
        catalogAfter = sys.ClaimCatalog.CurrentVersion;
        evalNotes = s.Log.Where(l => l.StartsWith("[outcome]")).Select(l => l["[outcome] ".Length..]).ToImmutableArray();
    }

    var forbidden = ForbiddenAudit.Run(c, run, GptForbidden.For(c.Id));
    var payload = run.Report.Abstention.Payload;

    rows.Add(new
    {
        id = c.Id, dimension = c.Dimension, level = c.Level, title = c.Title, prompt = c.Prompt, domain = c.Domain,
        facts = new
        {
            groundTruth = c.GroundTruth.ToString(), reflexive = c.Reflexive, risk = c.Risk.ToString(),
            attachedVerifiers = Attached(c), stageCount = Math.Max(1, c.Stages.Length),
            hasUtilities = !c.Actions.IsEmpty, evidenceCount = c.Evidence.Length,
            evidenceSources = c.Evidence.Select(e => e.Source.ToString()).Distinct().ToArray(),
            specialistTool = c.Specialist?.ToolId,
            catalogSeed = c.CatalogSeed.Select(f => f.Identity).ToArray(),
            mechanisms = c.Hypotheses.Select(h => h.Frame.Mechanism).Distinct().ToArray()
        },
        expected = new
        {
            disposition = c.Expected.Disposition.ToString(), code = c.Expected.Code.ToString(),
            strategy = c.Expected.Strategy?.ToString(), workflow = c.Expected.Workflow, critic = c.Expected.ExpectCritic
        },
        why = c.WhyRev2,
        result = new
        {
            abstained = run.Report.Abstention.Abstained,
            reasonCode = run.Report.Abstention.Code.ToString(),
            explanation = run.Report.Abstention.Explanation,
            payloadKind = payload?.Kind,
            payloadTypeOk = run.Report.Abstention.PayloadTypeMatches,
            payloadEvidence = payload?.EvidenceRefs.ToArray() ?? Array.Empty<string>(),
            payloadDetail = Describe(payload),
            strategy = s.Plan?.Strategy.ToString(), workflow = s.Plan?.WorkflowName,
            solverCount = s.Plan?.SolverCount ?? 0, includeCritic = s.Plan?.IncludeCritic ?? false,
            requireHuman = s.Plan?.RequireHumanApproval ?? false,
            rationale = s.Plan?.Rationale.ToArray() ?? Array.Empty<string>(),
            degradation = s.Degradation.ToString(),
            budget = s.Budget is { } b ? new { families = b.DistinctFamilies, vendors = b.DistinctVendors, effective = b.EffectiveIndependentAgents, ensembleOk = b.EnsembleClaimsPermitted } : null,
            roles = s.Roles.Select(r => new { role = r.Role, agent = r.AgentId, family = r.Family, vendor = r.Vendor }).ToArray(),
            grade = run.Report.ConservativeGrade(),
            capability = run.Report.Capability is { } cap ? new { level = cap.BestPassedLevel.ToString(), cap = cap.Cap, degraded = cap.Degraded, reasons = cap.DegradedReasons.ToArray() } : null,
            coverage = run.Report.Coverage is { } cv
                ? new { target = cv.TargetCoverage, set = cv.PredictionSet.ToArray() } : null,
            coverageNote = s.Log.FirstOrDefault(l => l.Contains("覆蓋層"))?.Trim(),
            stability = run.Report.Stability is { } st ? new
            {
                robustness = st.Robustness, worstCase = st.WorstCase, maxRegret = st.MaxRegret,
                resampling = st.DecisionStabilityUnderResampling,
                likertShiftStable = st.PosteriorOrderStableUnderLikertShift
            } : null,
            likelihoodSensitive = run.Report.LikelihoodSensitive,
            sensitivityDetail = s.SensitivityDetail,
            resamplingDetail = run.Journal.Events.OfType<StabilityResampled>().LastOrDefault()?.Detail,
            saturation = run.Report.HypothesisCoverage is { } sat ? new
            {
                singletons = sat.Singletons, doubletons = sat.Doubletons,
                estimatedUndiscovered = sat.EstimatedUndiscovered,
                coverage = DecisionAI.Modules.Assurance.Chao1SaturationEstimator.Coverage(s.Hypotheses.Count(), sat)
            } : null,
            skippedExperiments = run.Journal.Events.OfType<ExperimentSkipped>()
                .Select(e => new { draft = e.DraftId, evoi = e.Evoi, reason = e.Reason }).ToArray(),
            ensembleRationale = s.Log.Where(l => l.Contains("集成 ")).ToArray(),
            recommendedAction = s.Decision?.RecommendedAction,
            decisionReason = s.Decision?.Reason,
            perAction = s.Decision?.PerAction.OrderBy(k => k.Key).Select(k => new { id = k.Key, eu = k.Value.ExpectedUtility, worst = k.Value.WorstCase, regret = k.Value.MaxRegret, admissible = k.Value.Admissible }).ToArray(),
            beliefs = s.Beliefs.OrderByDescending(k => k.Value).Select(k => new { claim = k.Key, p = k.Value }).ToArray(),
            beliefsRendered = Checks.RenderBeliefs(s),
            claims = s.Claims.Select(k => new
            {
                id = k.LocalId, catalogId = k.Key.Value, match = k.Match.ToString(), matchScore = k.MatchScore,
                kind = k.Kind.ToString(), mechanism = k.Frame.Mechanism, locus = k.Frame.Locus,
                trigger = k.Frame.Trigger, observable = k.Frame.Observable, isOpinion = k.IsOpinion,
                proposals = k.Proposals.Select(p => new { agent = p.AgentId, likert = p.Confidence.ToString(), p = p.Probability }).ToArray(),
                evidenceFor = k.EvidenceFor.OrderBy(x => x).ToArray()
            }).ToArray(),
            verifications = s.Verifications.Select(v => new { verifier = v.VerifierId, level = v.Level.ToString(), claim = v.ClaimId, pass = v.Pass, score = v.Score, note = v.Note, disqualifies = v.Disqualifies }).ToArray(),
            experiments = s.Experiments.Select(e => new
            {
                id = e.Id, description = e.Description, nominatedBy = e.NominatedBy, registeredBy = e.RegisteredBy,
                source = e.LikelihoodSource, hash = e.LikelihoodHash[..12], outcomes = e.Outcomes.ToArray(), observed = e.ObservedOutcome,
                likert = e.LikertByClaim.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { claim = k.Key, levels = k.Value.Select(x => x.ToString()).ToArray() }).ToArray(),
                likelihoods = e.LikelihoodByClaim.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { claim = k.Key, row = k.Value.ToArray() }).ToArray()
            }).ToArray(),
            nominations = s.Nominations.Select(n => new { id = n.DraftId, description = n.Description, by = n.NominatedBy }).ToArray(),
            steps = s.PlannedSteps.Select(id => new { id, status = s.StepStatus.GetValueOrDefault(id, "—") }).ToArray(),
            halted = s.Halted, haltReason = s.HaltReason, skipped = s.SkippedSteps.ToArray(),
            log = s.Log.Where(l => !l.StartsWith("[outcome]")).ToArray()
        },
        execution = new
        {
            llmCalls = sys.LlmCallCount, cost = Math.Round(s.CostSpent, 4), budget = c.Budget,
            events = run.Journal.Events.Count, policyBefore = 0, policyAfter,
            catalogBefore = c.CatalogSeed.Length, catalogAfter,
            humanGates = run.Journal.Events.OfType<HumanActed>().Count(),
            eventKinds = run.Journal.Events.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()).Select(g => new { kind = g.Key, n = g.Count() }).ToArray(),
            evalNotes = evalNotes.ToArray()
        },
        checks = checks.Select(x => new { name = x.Name, pass = x.Pass, detail = x.Detail }).ToArray(),
        pass,
        forbidden = forbidden.Select(f => new { behavior = f.Behavior, status = f.Status, why = f.Why }).ToArray()
    });

    string mark = pass ? "PASS" : "FAIL";
    string disp = run.Report.Abstention.Abstained
        ? $"ABSTAIN {run.Report.Abstention.Code}/{payload?.Kind ?? "無"}"
        : $"{s.Plan!.Strategy}{(s.Plan.IncludeCritic ? "+critic" : "")}";
    Console.WriteLine($"{c.Id,-3} {mark}  {disp,-44} LLM {sys.LlmCallCount,2}  成本 {s.CostSpent,6:F3}  事件 {run.Journal.Events.Count,3}  {run.Report.ConservativeGrade()}");
}

// ══════════════════ Mutation testing ══════════════════
Console.WriteLine("\n── Mutation testing（壞的守門件必須被抓到）──");
var mutations = new List<object>();
foreach (var m in Mutations.All)
{
    var caught = new List<object>();
    int affected = 0, missed = 0;
    var caughtBy = new Dictionary<string, int>();

    foreach (var c in BenchmarkCases.All)
    {
        // 有些 mutation 需要配套的環境才觀察得到：M8 需要會寫歪的 briefer，M10 需要模型供給不足
        var baseOpts = m.Id switch
        {
            "M8"  => BaseOptions(c) with { Agents = () => ScriptedWorld.AgentsWithSkewedBriefer(c) },
            "M10" => BaseOptions(c) with { Agents = () => ScriptedWorld.AgentsWithFamilies(c, 1) },
            _     => BaseOptions(c)
        };
        var sys = TestSystem.Build(m.Apply(baseOpts));
        ImmutableArray<CheckResult> checks;
        try
        {
            var run = await sys.Orchestrator.RunAsync($"MUT-{m.Id}-{c.Id}", Request(c), c.Evidence);
            checks = Checks.RunAll(c, run, sys);
        }
        catch (Exception ex)
        {
            checks = ImmutableArray.Create(new CheckResult("Engine", false, ex.GetType().Name + ": " + ex.Message));
        }
        bool isAffected = m.Affected(c) && (m.Observable is null || m.Observable(baselines[c.Id]));
        if (isAffected) affected++;
        var bad = checks.Where(x => !x.Pass).Select(x => x.Name).ToArray();
        if (isAffected && bad.Length == 0) missed++;
        if (bad.Length > 0)
        {
            foreach (var n in bad) caughtBy[n] = caughtBy.GetValueOrDefault(n) + 1;
            caught.Add(new { id = c.Id, caughtBy = bad, detail = checks.First(x => !x.Pass).Detail });
        }
    }

    double? rate = m.NotObservableHere is not null || affected == 0 ? null : (affected - missed) / (double)affected;
    Console.WriteLine(rate is null
        ? $"{m.Id,-3} {m.Name,-34} 本 benchmark 不可觀察 —— {m.NotObservableHere}"
        : $"{m.Id,-3} {m.Name,-34} 受影響 {affected,2} 案，抓到 {affected - missed,2} 案（{rate:P0}）  由 {string.Join(", ", caughtBy.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}"))}");
    mutations.Add(new
    {
        id = m.Id, name = m.Name, target = m.Target, affectedWhy = m.AffectedWhy, rev2New = m.IsRev2New,
        notObservableHere = m.NotObservableHere, observableWhy = m.ObservableWhy,
        detected = caught.Count, affected, missed, total = BenchmarkCases.All.Length, rate,
        caughtBy = caughtBy.OrderByDescending(k => k.Value).Select(k => new { check = k.Key, n = k.Value }).ToArray(),
        cases = caught
    });
}

// ══════════════════ 降級階梯：同一題在不同模型供給下的行為 ══════════════════
Console.WriteLine("\n── 獨立性降級階梯（同一題，不同模型供給）──");
var ladderCase = BenchmarkCases.All.First(c => c.Id == "R3");
var ladder = new List<object>();
foreach (int families in new[] { 5, 3, 2, 1 })
{
    var opts = BaseOptions(ladderCase) with { Agents = () => ScriptedWorld.AgentsWithFamilies(ladderCase, families) };
    var run = await sys2Run(opts, ladderCase);
    var s = run.Run.State;
    Console.WriteLine($"  {families} 個模型家族 → {s.Degradation,-12} solver ×{s.Plan?.SolverCount ?? 0}  critic={(s.Plan?.IncludeCritic == true ? "有" : "無")}  " +
                      $"集成增益={(s.Budget?.EnsembleClaimsPermitted == true ? "准" : "不准")}  {run.Run.Report.ConservativeGrade()}");
    ladder.Add(new
    {
        families, degradation = s.Degradation.ToString(),
        solverCount = s.Plan?.SolverCount ?? 0, includeCritic = s.Plan?.IncludeCritic ?? false,
        strategy = s.Plan?.Strategy.ToString(), workflow = s.Plan?.WorkflowName,
        ensembleOk = s.Budget?.EnsembleClaimsPermitted ?? false,
        grade = run.Run.Report.ConservativeGrade(),
        degradedReasons = run.Run.Report.Capability?.DegradedReasons.ToArray() ?? Array.Empty<string>(),
        llmCalls = run.Calls, cost = Math.Round(s.CostSpent, 3)
    });
}

async Task<(DecisionAI.Orchestration.CaseRun Run, int Calls)> sys2Run(TestSystemOptions opts, BenchCase c)
{
    var sys = TestSystem.Build(opts);
    var run = await sys.Orchestrator.RunAsync($"LADDER-{c.Id}", Request(c), c.Evidence);
    return (run, sys.LlmCallCount);
}

var report = new
{
    benchmark = "DecisionAI Rev2 Phase 1 · Router Benchmark",
    version = "2.0",
    generatedAt = DateTime.UtcNow.ToString("O"),
    note = "LLM 為腳本化（ScriptedLlm），量的是控制流程、守門件與確定性引擎，不是模型品質。",
    summary = new { total = rows.Count, passed, abstained = abstainedCount, proceeded = rows.Count - abstainedCount },
    mutations,
    degradationLadder = ladder,
    cases = rows
};

JsonSerializerOptions opts2 = new()
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};
File.WriteAllText(outPath, JsonSerializer.Serialize(report, opts2));
Console.WriteLine($"\n{passed}/{rows.Count} 通過 → {Path.GetFullPath(outPath)}");

static string[] Attached(BenchCase c)
{
    var a = new List<string> { "L1_LlmCritic", "L2_Rule" };
    if (c.HasL3) a.Add("L3_ExecutableTest");
    if (c.HasL4) a.Add("L4_Experiment");
    return a.ToArray();
}

static object? Describe(DecisionAI.Core.Assurance.SubstitutePayload? p) => p switch
{
    DecisionAI.Core.Assurance.ScenarioBriefing sb => new
    {
        facts = sb.Facts.Select(f => new { f.Text, evidence = f.EvidenceFor.ToArray() }).ToArray(),
        scenarios = sb.Scenarios.Select(s => new
        {
            s.Name,
            basis = s.Basis.Select(b => new { b.Text, evidence = b.EvidenceFor.ToArray() }).ToArray(),
            indicators = s.Indicators.Select(i => new { i.Name, i.Source, i.Threshold, i.Frequency }).ToArray()
        }).ToArray(),
        risk = new { sb.Risk.Exposure, sb.Risk.ExitCondition }
    },
    DecisionAI.Core.Assurance.ConsiderationMap cm => new
    {
        items = cm.Items.Select(i => new { i.Name, i.WhoItMattersTo, evidence = i.EvidenceFor.ToArray() }).ToArray(),
        conflicts = cm.Conflicts.Select(x => new { x.Between, x.Nature }).ToArray(),
        forHumans = cm.ForHumans.Select(q => new { q.Question, q.WhoDecides }).ToArray()
    },
    DecisionAI.Core.Assurance.DefinitionMap dm => new
    {
        definitions = dm.Definitions.Select(d => new { d.Name, d.Statement, d.Consequence }).ToArray(),
        dm.WhyUndecidable
    },
    DecisionAI.Core.Assurance.DelegationHandoff dh => new
    {
        dh.ToolId, dh.WhyThisTool, dh.HowToRead, dh.UncertaintyNotes, inputs = dh.InputsRequired.ToArray()
    },
    DecisionAI.Core.Assurance.BlockedHandback bh => new
    {
        bh.WhatIsMissing, needed = bh.Needed.Select(n => new { n.What, n.WhyItWouldHelp }).ToArray(), bh.HowToResubmit
    },
    _ => null
};
