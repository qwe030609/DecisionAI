// ============================================================================
//  Rev2 Phase 1 完成門檻：
//    · 類別 1 / 2a / 2d / 4 的 golden assertion
//    · timeout 與未註冊 step 兩個缺口案
//    · MR-14（canonical id）、MR-17（context 重建）、MR-18（情境對稱性）在 MetamorphicTests.cs
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Testing;
using Xunit;

namespace DecisionAI.Tests;

public class GoldenTests
{
    // ── 類別 1：ATS 根因分析，完整閉環 ──
    [Fact]
    public async Task Category1_AtsRootCause_ReachesL4AndRecommendsRootCauseFix()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
        var s = run.State;

        Assert.False(run.Report.Abstention.Abstained);
        Assert.Equal("A", s.Decision!.RecommendedAction);
        Assert.Equal("H1", s.Beliefs.MaxBy(kv => kv.Value).Key);
        Assert.Equal(VerifierLevel.L4_Experiment, run.Report.Capability!.BestPassedLevel);
        Assert.Equal(0.95, run.Report.Capability.Cap);
        Assert.False(run.Report.Capability.Degraded);
        Assert.Null(run.Report.Coverage);                                       // Phase 1：覆蓋層留 null
        Assert.All(s.PlannedSteps, id => Assert.Equal("ok", s.StepStatus[id]));

        // 四元組合併：solver-B 用不同措辭描述同樣的 Mechanism|Locus，仍然合併成 H1 / H2
        Assert.Equal(new[] { "H1", "H2", "H3", "H4" }, s.Claims.Select(k => k.LocalId));
        Assert.Equal(Mechanisms.RaceCondition, s.ClaimByLocalId("H1")!.Frame.Mechanism);
        Assert.Equal(3, s.ClaimByLocalId("H1")!.Proposals.Count);               // A、B、C 都提了 H1
        Assert.True(s.ClaimByLocalId("H4")!.IsOpinion);                         // 無證據引用 → 意見
        Assert.DoesNotContain("H4", s.Beliefs.Keys);

        // Catalog：三條種子命中，H4 是新條目候選
        Assert.All(new[] { "H1", "H2", "H3" }, id => Assert.True(s.ClaimByLocalId(id)!.Key.IsCatalogued));
        Assert.False(s.ClaimByLocalId("H4")!.Key.IsCatalogued);
        Assert.Equal(MatchKind.New, s.ClaimByLocalId("H4")!.Match);

        // 證據永不進 system prompt，且被中性化區塊包住
        foreach (var call in sys.LlmCalls)
        {
            Assert.DoesNotContain("EV-001", call.System);
            if (call.User.Contains("EV-001")) Assert.Contains("<<<EVIDENCE id=EV-001", call.User);
        }
    }

    [Fact]
    public async Task Category1_PreRegistrationHash_IsWrittenBeforeL4AndCompared()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
        var events = run.Journal.Events;

        int registered = events.ToList().FindIndex(e => e is ExperimentPreRegistered);
        int observed   = events.ToList().FindIndex(e => e is ExperimentObserved);
        Assert.True(registered >= 0 && observed > registered, "似然 hash 必須在實驗執行前就寫入事件流");

        var exp = run.State.Experiments.Single();
        Assert.Equal(exp.RecomputeHash(), exp.LikelihoodHash);                  // 執行後仍相符
        Assert.Equal("likert_median", exp.LikelihoodSource);
        Assert.Equal("designer-E", exp.NominatedBy);
        Assert.NotEqual("designer-E", exp.RegisteredBy);                        // 提名與登記必須是不同角色
        Assert.All(exp.LikelihoodByClaim.Values.SelectMany(v => v),
            p => Assert.Contains(p, new[] { 0.10, 0.25, 0.50, 0.75, 0.90 }));   // 數字只能來自 Likert 查表
    }

    [Fact]
    public async Task Category1_TamperedLikelihood_IsRejectedBeforeRunning()
    {
        // 直接構造一個似然被事後竄改的實驗，確認守衛擋得下來
        var guard = new DecisionAI.Modules.Verification.PreRegistrationGuard();
        var good = MakeExperiment(0.90);
        guard.AssertUnmodified(good);                                           // 未竄改 → 通過

        var tampered = good with { LikelihoodByClaim = MakeExperiment(0.10).LikelihoodByClaim };
        var ex = Assert.Throws<DecisionAI.Modules.Verification.PreRegistrationViolationException>(
            () => guard.AssertUnmodified(tampered));
        Assert.Contains("拒跑", ex.Message);
        await Task.CompletedTask;
    }

    private static Experiment MakeExperiment(double p)
    {
        var outcomes = ImmutableArray.Create("下降", "無變化");
        var lik = ImmutableDictionary<string, ImmutableArray<double>>.Empty.Add("H1", ImmutableArray.Create(p, 1 - p));
        return new Experiment("EXP-1", "停用 X 後重跑", outcomes, lik,
            ImmutableDictionary<string, ImmutableArray<LikertBelief>>.Empty, "designer-E", "elicitor",
            Experiment.ComputeHash("停用 X 後重跑", outcomes, lik), "likert_median", null);
    }

    [Fact]
    public async Task Category1_OutcomeFeedback_AdvancesPolicyAndCatalogWithoutTouchingRunningCase()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
        Assert.Equal(0, run.State.PolicyVersion);
        long catalogBefore = sys.ClaimCatalog.CurrentVersion;

        var truth = run.State.Claims.ToImmutableDictionary(k => k.LocalId, k => k.LocalId == "H1");
        var next = sys.Orchestrator.RecordOutcome(run.Journal, new Outcome("H1", truth, VerifierLevel.L5_RealOutcome, "部署 A"));

        Assert.Equal(1, next.Version);
        Assert.Equal(0, run.State.PolicyVersion);                               // 進行中的 case 仍 pin 在 v0
        Assert.Equal(2.0 / 3, next.Weight("solver-A", AtsScenario.Domain, "solver").Mean, 3);
        Assert.Equal(1.0 / 3, next.Weight("solver-B", AtsScenario.Domain, "solver").Mean, 3);
        Assert.Empty(sys.PolicyStore.Pin(0).Weights);                           // v0 快照不可變
        Assert.Equal(catalogBefore, sys.ClaimCatalog.CurrentVersion);           // 真假設已在 catalog 裡 → 不新增條目
        Assert.Contains(run.Journal.Events, e => e is CatalogEntryProposed);
    }

    // ── 類別 2a：沒有 ground truth → 拒答，但給出型別化的替代交付 ──
    [Fact]
    public async Task Category2a_NoGroundTruth_AbstainsWithConsiderationMap()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-2A", new DecisionRequest(
            "公司 logo 該用藍色還是綠色？", "定案", "creative", new Constraints(1, 60, RiskLevel.Low),
            GroundTruth: GroundTruthStatus.Undefined), AtsScenario.Evidence());

        AssertAbstained(run.Report, ReasonCode.NoGroundTruth);
        Assert.Null(run.State.Plan);                                            // 沒有走到策略路由
        var payload = run.Report.Abstention.Payload;
        Assert.NotNull(payload);
        Assert.True(payload!.ValidFor.Contains(ReasonCode.NoGroundTruth));       // 型別綁定
    }

    // ── 類別 2d：Triage 拒絕 ──
    [Fact]
    public async Task Category2d_NoRealVerifier_AbstainsWithBlockedHandbackAndZeroLlmCost()
    {
        var sys = TestSystem.Build(new TestSystemOptions { IncludeL3Executable = false, IncludeL4Experiment = false });
        var run = await sys.Orchestrator.RunAsync("CASE-2D", AtsScenario.Request(), AtsScenario.Evidence());

        AssertAbstained(run.Report, ReasonCode.NoVerifier);
        Assert.Equal(0, sys.LlmCallCount);                                      // 這個 payload 是確定性的 → 零模型成本
        Assert.Equal(0, run.State.CostSpent);
        Assert.Equal(VerifierLevel.L2_Rule, run.State.Facts!.BestAvailableVerifier);
        var handback = Assert.IsType<BlockedHandback>(run.Report.Abstention.Payload);
        Assert.Contains(handback.Needed, n => n.What.Contains("L3"));
    }

    [Fact]
    public async Task Category2d_DefinitionalDispute_AbstainsWithDefinitionMap()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-2D2",
            AtsScenario.Request() with { GroundTruth = GroundTruthStatus.Contested }, AtsScenario.Evidence());
        AssertAbstained(run.Report, ReasonCode.DefinitionalDispute);
        Assert.True(run.Report.Abstention.PayloadTypeMatches);
    }

    [Fact]
    public async Task Reflexive_DirectionPrediction_IsRefusedAndGetsSymmetricScenarioBriefing()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-3B", new DecisionRequest(
            "根據物理動量模型，下週某股價的動量方向為何？", "決定加碼", "physics",
            new Constraints(1, 60, RiskLevel.High), Reflexive: true), AtsScenario.Evidence());

        AssertAbstained(run.Report, ReasonCode.ReflexiveDirection);
        var sb = Assert.IsType<ScenarioBriefing>(run.Report.Abstention.Payload);
        Assert.True(sb.Scenarios.Length >= 2, "少於 2 個情境等於變相的方向預測");
        Assert.All(sb.Scenarios.SelectMany(x => x.Indicators), i => Assert.True(i.IsObservable));
        Assert.All(sb.EvidenceRefs, id => Assert.Contains(id, run.State.Evidence.Select(e => e.Id)));
    }

    // ── Tool/Model Router ──
    [Fact]
    public async Task SpecialistDomain_IsDelegated_WithHandoffAndNoFabricatedNumbers()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-TOOL", ReliabilityRequest(), Array.Empty<EvidenceDraft>());

        AssertAbstained(run.Report, ReasonCode.DelegatedToExternalModel);
        var h = Assert.IsType<DelegationHandoff>(run.Report.Abstention.Payload);
        Assert.Equal("weibull_reliability", h.ToolId);
        Assert.NotEmpty(h.InputsRequired);
        Assert.Equal(0, sys.LlmCallCount);                                      // 轉介不需要任何模型呼叫
        Assert.Null(run.State.Plan);
    }

    [Fact]
    public async Task SpecialistDomain_WithoutTheTool_IsNotDelegated()
    {
        var sys = TestSystem.Build(new TestSystemOptions
        {
            ToolRouter = new DecisionAI.Modules.Routing.ToolModelRouter(new[] { "spice" })   // 現場沒有 weibull
        });
        var run = await sys.Orchestrator.RunAsync("CASE-TOOL2", ReliabilityRequest(), AtsScenario.Evidence());
        Assert.NotEqual(ReasonCode.DelegatedToExternalModel, run.Report.Abstention.Code);
        Assert.Contains("沒有這個工具", run.State.Delegation!.Reason);
    }

    internal static DecisionRequest ReliabilityRequest() => new(
        "某繼電器已運行 80 萬次，未來一個月故障機率是多少？", "決定是否計畫性更換", "reliability",
        new Constraints(1.0, 60, RiskLevel.Medium),
        Specialist: new SpecialistDomain("weibull_reliability", "壽命分布有成熟的參數模型",
            "取 shape/scale 與條件失效機率", "右設限比例高時區間會很寬",
            ImmutableArray.Create("同型號失效循環數清單", "目前累計循環數")));

    // ── 獨立性降級階梯（Rev2 D10）──
    [Theory]
    [InlineData(3, DegradationLevel.None, true)]
    [InlineData(2, DegradationLevel.Reduced, true)]
    [InlineData(1, DegradationLevel.Minimal, false)]
    public async Task IndependenceBudget_DrivesDegradationAndAssurance(int families, DegradationLevel expected, bool ensembleAllowed)
    {
        var sys = TestSystem.Build(new TestSystemOptions { Agents = () => PoolWith(families) });
        var run = await sys.Orchestrator.RunAsync("CASE-DEG", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.Equal(expected, run.State.Degradation);
        Assert.Equal(ensembleAllowed, run.State.Budget!.EnsembleClaimsPermitted);
        if (!ensembleAllowed)
        {
            Assert.True(run.Report.Capability!.Degraded, "不准宣稱集成增益時，能力層必須誠實降級");
            Assert.Contains(run.Report.Capability.DegradedReasons, r => r.Contains("集成增益"));
            Assert.True(run.State.Plan!.SolverCount <= 2, "獨立性不足時多跑 solver 只是把成本乘以 N");
        }
    }

    [Fact]
    public async Task SingleModelPool_DisablesIndependenceDependentStrategies()
    {
        var sys = TestSystem.Build(new TestSystemOptions
        {
            Agents = () => PoolWith(1).Take(1),
            IncludeL1Critic = false        // 只有一個模型 → 它已經是 solver，不可能再當 critic
        });
        var run = await sys.Orchestrator.RunAsync("CASE-SOLO", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.Equal(DegradationLevel.SingleAgent, run.State.Degradation);
        Assert.Equal(Strategy.SingleAgent, run.State.Plan!.Strategy);
        Assert.Equal(1, run.State.Plan.SolverCount);
        Assert.False(run.State.Plan.IncludeCritic);
        Assert.Contains(run.State.Plan.Rationale, r => r.Contains("禁用依賴獨立性的策略"));
    }

    /// <summary>n 個家族的池：家族數決定降級等級，池大小固定 3。</summary>
    private static IEnumerable<(AgentSpec, ILlm)> PoolWith(int families)
    {
        var all = AtsScenario.Agents().Where(a => a.Spec.EligibleRoles.Contains("solver")).Take(3).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var spec = all[i].Spec;
            string family = $"fam-{i % families}";
            yield return (spec with
            {
                BaseModelFamily = family,
                Vendor = $"vendor-{i % families}",
                EligibleRoles = ImmutableArray.Create("solver", "critic", "experiment_designer", "briefer")
            }, all[i].Llm);
        }
    }

    // ── 類別 4：流水線邊界閘 + halt 傳播 ──
    [Fact]
    public async Task Category4_HumanRejectsStageBoundary_DownstreamNeverRuns()
    {
        var human = new ScriptedHumanGateway(req => req.StepId == "s1_gate"
            ? new HumanVerdict(false, "R ± σ 的 σ 沒有給出估計方法")
            : new HumanVerdict(true, "ok"));
        var sys = TestSystem.Build(new TestSystemOptions { Human = human });
        var run = await sys.Orchestrator.RunAsync("CASE-004", PipelineRequest(), Array.Empty<EvidenceDraft>());
        var s = run.State;

        Assert.True(s.Halted);
        Assert.Contains("人類拒絕", s.HaltReason);
        Assert.Equal(new[] { "s2_agent", "s2_verify", "s2_gate" }, s.SkippedSteps);
        Assert.DoesNotContain("s2_agent", s.StepStatus.Keys);
        Assert.Equal(2, s.Claims.Count);
        AssertAbstained(run.Report, ReasonCode.PipelineHalted);
        var handback = Assert.IsType<BlockedHandback>(run.Report.Abstention.Payload);
        Assert.Contains(handback.Needed, n => n.What.Contains("s2_agent"));
    }

    [Fact]
    public async Task Category4_AllGatesApproved_CapabilityIsBoundedByWhatActuallyPassed()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-004", PipelineRequest(), Array.Empty<EvidenceDraft>());
        Assert.False(run.State.Halted);
        Assert.Equal(new[] { "C1", "C2", "C3" }, run.State.Claims.Select(k => k.LocalId));
        Assert.All(run.State.Claims, k => Assert.Equal(Mechanisms.StageDeliverable, k.Frame.Mechanism));
        Assert.Equal(VerifierLevel.None, run.Report.Capability!.BestPassedLevel);
        Assert.Equal(0.0, run.Report.Capability.Cap);
    }

    // ── 缺口案 1：未註冊 step type → 整個 DAG 不執行 ──
    [Fact]
    public async Task Workflow_UnregisteredStepType_RejectsWholeDagBeforeAnyStep()
    {
        var catalog = new Dictionary<string, WorkflowDefinition>
        {
            ["engineering_root_cause"] = WorkflowDefinition.FromJson("""
                {"name":"engineering_root_cause","steps":[
                  {"id":"solvers","type":"parallel_agents","params":{"role":"solver"}},
                  {"id":"magic",  "type":"quantum_oracle","dependsOn":["solvers"]},
                  {"id":"decide", "type":"decision","dependsOn":["magic"]}]}
                """)
        };
        var sys = TestSystem.Build(new TestSystemOptions { Catalog = catalog });
        var run = await sys.Orchestrator.RunAsync("CASE-FC", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.True(run.State.Halted);
        Assert.Contains("quantum_oracle", run.State.HaltReason);
        Assert.Empty(run.State.StepStatus);                                     // 連 solvers 都沒跑
        Assert.Equal(new[] { "solvers", "magic", "decide" }, run.State.SkippedSteps);
        Assert.Equal(0, sys.LlmCallCount);
        AssertAbstained(run.Report, ReasonCode.PipelineHalted);
    }

    // ── 缺口案 2：timeout 降級 vs fail-closed ──
    [Fact]
    public async Task Workflow_Timeout_DegradeWithPartial_ContinuesAndDegradesCapability()
    {
        var sys = TestSystem.Build(new TestSystemOptions { Catalog = TimeoutCatalog(TimeoutAction.DegradeWithPartial), ExtraHandlers = RegisterSlowStep });
        var run = await sys.Orchestrator.RunAsync("CASE-TO", AtsScenario.Request(), AtsScenario.Evidence());
        var s = run.State;

        Assert.False(s.Halted);
        Assert.Equal("partial", s.StepStatus["slow"]);
        Assert.Contains("slow", s.PartialSteps);
        Assert.Equal("ok", s.StepStatus["decide"]);
        Assert.NotNull(s.Decision);
        Assert.True(run.Report.Capability!.Degraded);
        Assert.Contains(run.Report.Capability.DegradedReasons, r => r.Contains("逾時"));
        Assert.Contains(run.Journal.Events, e => e is PartialResult { StepId: "slow" });
    }

    [Fact]
    public async Task Workflow_Timeout_FailClosed_HaltsAndSkipsDownstream()
    {
        var sys = TestSystem.Build(new TestSystemOptions { Catalog = TimeoutCatalog(TimeoutAction.FailClosed), ExtraHandlers = RegisterSlowStep });
        var run = await sys.Orchestrator.RunAsync("CASE-TO2", AtsScenario.Request(), AtsScenario.Evidence());
        var s = run.State;

        Assert.True(s.Halted);
        Assert.Equal("failed", s.StepStatus["slow"]);
        Assert.Contains("decide", s.SkippedSteps);
        Assert.Null(s.Decision);
        AssertAbstained(run.Report, ReasonCode.PipelineHalted);
    }

    // ── helpers ──
    internal static void AssertAbstained(AssuranceReport r, ReasonCode code)
    {
        Assert.True(r.Abstention.Abstained);
        Assert.Equal(code, r.Abstention.Code);
        Assert.Null(r.Capability);                                              // 拒答時不輸出任何信心相關數值
        Assert.Null(r.Coverage);
        Assert.Null(r.Stability);
        Assert.True(r.Abstention.PayloadTypeMatches);
        Assert.StartsWith("ABSTAIN", r.ConservativeGrade());
    }

    internal static DecisionRequest PipelineRequest() => new(
        "量測待測物等效電阻並出報告", "三段流水線", "instrument_control", new Constraints(2.0, 120, RiskLevel.Medium),
        Stages: ImmutableArray.Create(
            new StageSpec("量測", "設計 SCPI 量測序列", VerifierLevel.L3_ExecutableTest, "輸出：CSV 欄位 [t, V, I]"),
            new StageSpec("分析", "由量測資料估算等效電阻", VerifierLevel.L3_ExecutableTest, "輸出：R ± σ"),
            new StageSpec("報告", "產出結論段落", VerifierLevel.L1_LlmCritic, "輸出：≤ 200 字")));

    private static Dictionary<string, WorkflowDefinition> TimeoutCatalog(TimeoutAction action) => new()
    {
        ["engineering_root_cause"] = WorkflowDefinition.FromJson($$$"""
            {"name":"engineering_root_cause","steps":[
              {"id":"solvers","type":"parallel_agents","params":{"role":"solver"}},
              {"id":"canon",  "type":"claim_canonicalize","dependsOn":["solvers"]},
              {"id":"rules",  "type":"verify","dependsOn":["canon"],"params":{"level":"2"}},
              {"id":"test",   "type":"verify","dependsOn":["rules"],"params":{"level":"3"}},
              {"id":"prior",  "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
              {"id":"slow",   "type":"slow_step","dependsOn":["prior"],"maxRetries":0,"timeout":{"seconds":1,"onTimeout":"{{{action}}}"}},
              {"id":"decide", "type":"decision","dependsOn":["slow"]}]}
            """)
    };

    private static void RegisterSlowStep(DecisionAI.Modules.Workflow.WorkflowEngine engine)
        => engine.RegisterHandler("slow_step", async (step, state, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Array.Empty<CaseEvent>();
        });
}
