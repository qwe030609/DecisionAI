// ============================================================================
//  TestSystem — 組合根（手動 DI）。每個守門件都是建構子參數，可在這裡換成 broken 版本。
//  Host 與測試都用它；正式版 Host 換成 DI 容器時，建構子不變。
// ============================================================================

using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Decision;
using DecisionAI.Modules.Diversity;
using DecisionAI.Modules.Humans;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Evidence;
using DecisionAI.Modules.Journal;
using DecisionAI.Modules.Probability;
using DecisionAI.Modules.Routing;
using DecisionAI.Modules.Verification;
using DecisionAI.Modules.Workflow;
using DecisionAI.Orchestration;

namespace DecisionAI.Testing;

public sealed record TestSystemOptions
{
    public bool IncludeL1Critic { get; init; } = true;
    public bool IncludeL2Rule { get; init; } = true;
    public bool IncludeL3Executable { get; init; } = true;
    public bool IncludeL4Experiment { get; init; } = true;

    // ── 守門件：全部可換成 broken 版本（mutation switch）──
    public IHumanGateway? Human { get; init; }
    public IRolePermission? Permission { get; init; }
    public IVerifiabilityTriage? Triage { get; init; }
    public IToolModelRouter? ToolRouter { get; init; }
    public IRoleAssigner? RoleAssigner { get; init; }
    /// <summary>mutation switch：Thompson 改回 argmax mean（要有 registry 才能建，所以用旗標）。</summary>
    public bool ArgmaxAssigner { get; init; }
    public bool IgnoreDegradation { get; init; }          // mutation switch：永遠宣稱完整獨立性
    public IAbstentionGate? Gate { get; init; }
    public IAssuranceService? Assurance { get; init; }
    public IInjectionGuard? Guard { get; init; }
    public IClaimCanonicalizer? Canonicalizer { get; init; }
    public IPreRegistrationGuard? PreRegGuard { get; init; }
    public IPayloadVerifier? PayloadVerifier { get; init; }

    // ── Phase 2 的守門件，同樣全部可換 ──
    public ICalibrator? Calibrator { get; init; }
    public ICorrelationEstimator? Correlation { get; init; }
    public IExperimentSelector? Selector { get; init; }
    public ILikelihoodSensitivityAnalyzer? Sensitivity { get; init; }
    public IDecisionStabilityAnalyzer? Stability { get; init; }
    public IHypothesisSaturationEstimator? Saturation { get; init; }
    public IConformalCalibrator? Conformal { get; init; }
    public IDiversityMonitor? DiversityMonitor { get; init; }
    public IPresentationPolicy? Presentation { get; init; }
    public IExperimentCatalog? ExperimentCatalog { get; init; }

    // ── Phase 3：學習與稽核 ──
    public IConformalSampleStore? ConformalStore { get; init; }
    public IDriftAlarm? Drift { get; init; }
    public IAutomationBiasMonitor? Oversight { get; init; }
    public IPolicyStore? PolicyStore { get; init; }
    public Func<CaseState, IInjectionGuard, string>? CriticContext { get; init; }   // MR-17：餵原始輸出必須被擋

    public IReadOnlyDictionary<string, WorkflowDefinition>? Catalog { get; init; }
    public Action<WorkflowEngine>? ExtraHandlers { get; init; }
    public Func<VerifierRegistry, IEnumerable<IVerifier>>? ExtraVerifiers { get; init; }
    public Func<IEnumerable<(AgentSpec Spec, ILlm Llm)>>? Agents { get; init; }
    public IClaimCatalog? ClaimCatalog { get; init; }
    public Func<Claim, CaseState, (bool, double, string)?>? L3Test { get; init; }
    public Func<Experiment, CaseState, CancellationToken, Task<int>>? L4Run { get; init; }
    public int Seed { get; init; } = 12345;
}

public sealed class TestSystem
{
    public required CaseOrchestrator Orchestrator { get; init; }
    public required AgentRegistry Registry { get; init; }
    public required IPolicyStore PolicyStore { get; init; }
    public required IClaimCatalog ClaimCatalog { get; init; }
    public required WorkflowEngine Engine { get; init; }
    public required IRolePermission Permission { get; init; }
    public required IClock Clock { get; init; }
    public required AtsSimulator World { get; init; }
    public required IReadOnlyList<ILlm> Llms { get; init; }
    public required IHumanGateway Human { get; init; }
    public required IExperimentCatalog ExperimentCatalog { get; init; }
    public required IConformalSampleStore ConformalStore { get; init; }
    public required IAutomationBiasMonitor Oversight { get; init; }
    /// <summary>序列型量測要看「系統實際用的那一個」，不是自己再 new 一個。</summary>
    public required ICalibrator Calibrator { get; init; }

    public int LlmCallCount => Llms.OfType<ScriptedLlm>().Sum(l => l.Calls.Count);
    public IEnumerable<LlmRequest> LlmCalls => Llms.OfType<ScriptedLlm>().SelectMany(l => l.Calls);

    public static TestSystem Build(TestSystemOptions? o = null)
    {
        o ??= new TestSystemOptions();
        var clock = new FixedClock();
        var rng = new DecisionAI.Adapters.Runtime.SeededRandomSource(o.Seed);
        var policy = o.PolicyStore ?? new InMemoryPolicyStore();
        var claimCatalog = o.ClaimCatalog ?? AtsScenario.SeededCatalog();
        var permission = o.Permission ?? new RolePermissionMatrix();
        var evidence = new InMemoryEvidenceStore(clock);
        var guard = o.Guard ?? new InjectionGuard();
        var runner = new AgentRunner();
        var world = new AtsSimulator();
        var human = o.Human ?? ScriptedHumanGateway.AlwaysApprove();

        var registry = new AgentRegistry();
        var llms = new List<ILlm>();
        foreach (var (spec, llm) in (o.Agents?.Invoke() ?? AtsScenario.Agents().Select(a => (a.Spec, (ILlm)a.Llm))))
        { registry.Register(spec, llm); llms.Add(llm); }

        var verifiers = new VerifierRegistry();
        if (o.IncludeL2Rule)       verifiers.Register(new RuleVerifier(evidence));
        if (o.IncludeL1Critic)     verifiers.Register(new LlmCriticVerifier(registry, runner, guard, policy, o.CriticContext));
        if (o.IncludeL3Executable) verifiers.Register(new ExecutableTestVerifier("sim", o.L3Test ?? world.TestHypothesis));
        if (o.IncludeL4Experiment) verifiers.Register(new ExperimentVerifier(evidence,
            o.L4Run ?? ((exp, _, _) => Task.FromResult(world.RunExperiment(exp))), o.PreRegGuard ?? new PreRegistrationGuard()));
        if (o.ExtraVerifiers is not null) foreach (var v in o.ExtraVerifiers(verifiers)) verifiers.Register(v);

        var canonicalizer = o.Canonicalizer ?? new ClaimCanonicalizer();
        var elicitor = new LikertLikelihoodElicitor();

        // 集成器帶校準與相關性折扣：多個同 family 的 agent 講同一句話，不該算成多個獨立證據
        var calibrator = o.Calibrator ?? new CalibrationEngine();
        var ensembler = new CalibratedEnsembler(calibrator, o.Correlation ?? new BlendedCorrelation());
        var bayes = new TemperedBayesUpdater();
        var decision = new DecisionEngine();
        var experimentCatalog = o.ExperimentCatalog ?? new InMemoryExperimentCatalog();
        var conformalStore = o.ConformalStore ?? new InMemoryConformalSampleStore();
        var oversight = o.Oversight ?? new AutomationBiasMonitor();

        var kit = new Phase2Kit
        {
            Selector          = o.Selector ?? new EvoiExperimentSelector(),
            Sensitivity       = o.Sensitivity ?? new LikelihoodSensitivityAnalyzer(bayes),
            Stability         = o.Stability,     // null → StandardHandlers 用現成的 ensembler/bayes/decision 組
            Saturation        = o.Saturation ?? new Chao1SaturationEstimator(),
            Conformal         = o.Conformal ?? new ConformalCalibrator(),
            Diversity         = o.DiversityMonitor ?? new DiversityMonitor(),
            Presentation      = o.Presentation ?? new PresentationPolicy(),
            ExperimentCatalog = experimentCatalog,
            Oversight         = oversight
        };

        var engine = new WorkflowEngine();
        new StandardHandlers(registry, runner, verifiers, ensembler, bayes, decision,
                             human, guard, rng, policy, claimCatalog, canonicalizer, elicitor, kit)
            .RegisterAll(engine);
        o.ExtraHandlers?.Invoke(engine);

        var payloads = new PayloadBuilder(registry, runner, guard,
            o.PayloadVerifier ?? new PayloadVerifier(), evidence, policy);

        var orchestrator = new CaseOrchestrator(
            policy, claimCatalog, permission, clock, evidence, verifiers,
            o.Triage ?? new VerifiabilityTriage(),
            o.ToolRouter ?? new ToolModelRouter(),
            WrapAssigner(o, registry),
            new StrategyRouter(registry, null, o.Drift), engine,
            o.Assurance ?? new AssuranceService(o.Gate ?? new AbstentionGate()),
            new EvaluationService(), new CatalogWriter(claimCatalog), payloads,
            o.Catalog ?? WorkflowCatalog.Default(), rng, experimentCatalog, conformalStore);

        return new TestSystem
        {
            Orchestrator = orchestrator, Registry = registry, PolicyStore = policy, ClaimCatalog = claimCatalog,
            Engine = engine, Permission = permission, Clock = clock, World = world, Llms = llms, Human = human,
            ExperimentCatalog = experimentCatalog, ConformalStore = conformalStore, Oversight = oversight,
            Calibrator = calibrator
        };
    }

    private static IRoleAssigner WrapAssigner(TestSystemOptions o, AgentRegistry registry)
    {
        var inner = o.RoleAssigner ?? (o.ArgmaxAssigner
            ? new ArgmaxMeanAssigner(registry) : (IRoleAssigner)new RoleAssigner(registry));
        return o.IgnoreDegradation ? new IgnoreDegradationAssigner(inner) : inner;
    }
}
