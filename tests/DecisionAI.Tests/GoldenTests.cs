// ============================================================================
//  Phase 1 完成門檻：類別 1 / 2a / 2d / 4 的 golden assertion + timeout 與未註冊 step 兩個缺口案。
//  類別對照（依 Rev1 架構文件 §4 的測試掛鉤）：
//    1  = 有可執行 verifier 的工程題（ATS 根因）
//    2a = 無 ground truth（價值觀）→ 正確拒答
//    2d = Triage 拒絕（無 verifier / 定義爭議）
//    4  = 跨領域流水線的邊界攔截 + halt 傳播
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
        Assert.True(s.Beliefs["H1"] > 0.8, $"H1 應在兩次實驗後 > 0.8，實際 {s.Beliefs["H1"]:F2}");
        Assert.Equal(VerifierLevel.L4_Experiment, run.Report.Capability!.BestPassedLevel);
        Assert.Equal(0.95, run.Report.Capability.Cap);
        Assert.False(run.Report.Capability.Degraded);
        Assert.Null(run.Report.Coverage);                                       // Phase 1：覆蓋層留 null
        Assert.Equal(1.0, run.Report.Stability!.Robustness);
        Assert.All(s.PlannedSteps, id => Assert.Equal("ok", s.StepStatus[id]));

        // 主張合併與 ID 決定性：solver-A 首先提出 H1；H4「PLC 端主動斷線」無證據 → 意見
        Assert.Equal(new[] { "H1", "H2", "H3", "H4" }, s.Claims.Select(k => k.Id));
        Assert.Equal(3, s.Claims[0].Proposals.Count);
        Assert.True(s.Claims[3].IsOpinion);
        Assert.False(s.Claims[2].IsOpinion);                                    // H3 支持度低但有引用 → 不是意見
        Assert.DoesNotContain("H4", s.Beliefs.Keys);

        // 證據永不進 system prompt，且被中性化區塊包住
        foreach (var call in sys.Llms.OfType<ScriptedLlm>().SelectMany(l => l.Calls))
        {
            Assert.DoesNotContain("EV-001", call.System);
            if (call.User.Contains("EV-001")) Assert.Contains("<<<EVIDENCE id=EV-001", call.User);
        }
    }

    [Fact]
    public async Task Category1_OutcomeFeedback_AdvancesPolicyWithoutTouchingRunningCase()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
        Assert.Equal(0, run.State.PolicyVersion);

        var truth = run.State.Claims.ToImmutableDictionary(k => k.Id, k => k.Id == "H1");
        var next = sys.Orchestrator.RecordOutcome(run.Journal, new Outcome("H1", truth, VerifierLevel.L5_RealOutcome, "部署 A"));

        Assert.Equal(1, next.Version);
        Assert.Equal(0, run.State.PolicyVersion);                               // 進行中的 case 仍 pin 在 v0
        Assert.Equal(2.0 / 3, next.Weight("solver-A", AtsScenario.Domain, "solver").Mean, 3);
        Assert.Equal(1.0 / 3, next.Weight("solver-B", AtsScenario.Domain, "solver").Mean, 3);
        Assert.Empty(sys.PolicyStore.Pin(0).Weights);                              // v0 快照不可變
        Assert.Equal(new[] { "solver-A", "solver-C", "solver-B" },
            sys.Registry.Select(new("solver", AtsScenario.Domain, 3), next).Select(a => a.Spec.AgentId));
    }

    // ── 類別 2a：沒有 ground truth → 拒答，且不花任何 LLM token ──
    [Fact]
    public async Task Category2a_NoGroundTruth_AbstainsBeforeAnyLlmCall()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-2A", new DecisionRequest(
            "公司 logo 該用藍色還是綠色？", "定案", "creative", new Constraints(1, 60, RiskLevel.Low),
            GroundTruth: GroundTruthStatus.Undefined), Array.Empty<EvidenceDraft>());

        AssertAbstained(run.Report, ReasonCode.NoGroundTruth);
        Assert.Equal(0, sys.LlmCallCount);
        Assert.Equal(0, run.State.CostSpent);
        Assert.Null(run.State.Plan);
        Assert.NotNull(run.Report.Abstention.Alternative);
    }

    // ── 類別 2d：Triage 拒絕 ──
    [Fact]
    public async Task Category2d_NoRealVerifier_AbstainsWithNoVerifier()
    {
        var sys = TestSystem.Build(new TestSystemOptions { IncludeL3Executable = false, IncludeL4Experiment = false });
        var run = await sys.Orchestrator.RunAsync("CASE-2D", AtsScenario.Request(), AtsScenario.Evidence());

        AssertAbstained(run.Report, ReasonCode.NoVerifier);
        Assert.Equal(0, sys.LlmCallCount);                                      // 只有 L1/L2 → 連 solver 都不跑
        Assert.Equal(VerifierLevel.L2_Rule, run.State.Facts!.BestAvailableVerifier);
    }

    [Fact]
    public async Task Category2d_DefinitionalDispute_AbstainsEvenWithVerifiers()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-2D2", AtsScenario.Request() with { GroundTruth = GroundTruthStatus.Contested }, AtsScenario.Evidence());
        AssertAbstained(run.Report, ReasonCode.DefinitionalDispute);
        Assert.Equal(0, sys.LlmCallCount);
    }

    [Fact]
    public async Task Reflexive_DirectionPrediction_IsRefusedRegardlessOfWrapping()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-3B", new DecisionRequest(
            "根據物理動量模型，下週某股價的動量方向為何？", "決定加碼", "physics", new Constraints(1, 60, RiskLevel.High),
            Reflexive: true), Array.Empty<EvidenceDraft>());
        AssertAbstained(run.Report, ReasonCode.ReflexiveDirection);
        Assert.Equal(0, sys.LlmCallCount);
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
        Assert.Equal(2, s.Claims.Count);                                        // 只有 s0、s1 的交付物
        Assert.Equal(new[] { "s0_gate", "s1_gate" }, human.Requests.Select(r => r.StepId));
        AssertAbstained(run.Report, ReasonCode.PipelineHalted);
        Assert.Contains("s2_agent", run.Report.Abstention.Alternative!);
        Assert.Equal(2, sys.LlmCallCount);                                      // s2 的 LLM 沒被呼叫
    }

    [Fact]
    public async Task Category4_AllGatesApproved_CapabilityIsBoundedByWeakestStageVerifier()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-004", PipelineRequest(), Array.Empty<EvidenceDraft>());
        Assert.False(run.State.Halted);
        Assert.Equal(new[] { "C1", "C2", "C3" }, run.State.Claims.Select(k => k.Id));
        Assert.All(run.State.Claims, k => Assert.Equal(ClaimKind.Candidate, k.Kind));
        // ATS 模擬器對段落交付物不適用 → L3 沒有通過紀錄；L1 critic 引用的 H1..H4 不存在 → 全部丟掉 → 什麼都沒通過
        Assert.Equal(VerifierLevel.None, run.Report.Capability!.BestPassedLevel);
        Assert.Equal(0.0, run.Report.Capability.Cap);
        Assert.Contains(run.State.Log, l => l.Contains("critic 引用不存在的主張"));
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
        Assert.Equal("ok", s.StepStatus["decide"]);                             // 下游照跑
        Assert.NotNull(s.Decision);
        Assert.True(run.Report.Capability!.Degraded);                           // 能力層必須降級
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
    private static void AssertAbstained(AssuranceReport r, ReasonCode code)
    {
        Assert.True(r.Abstention.Abstained);
        Assert.Equal(code, r.Abstention.Code);
        Assert.Null(r.Capability);                                              // 拒答時不輸出任何信心相關數值
        Assert.Null(r.Coverage);
        Assert.Null(r.Stability);
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
              {"id":"rules",  "type":"verify","dependsOn":["solvers"],"params":{"level":"2"}},
              {"id":"test",   "type":"verify","dependsOn":["rules"],"params":{"level":"3"}},
              {"id":"prior",  "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
              {"id":"slow",   "type":"slow_step","dependsOn":["prior"],"maxRetries":0,"timeout":{"seconds":1,"onTimeout":"{{{action}}}"}},
              {"id":"decide", "type":"decision","dependsOn":["slow"]}]}
            """)
    };

    private static void RegisterSlowStep(DecisionAI.Modules.Workflow.WorkflowEngine engine)
        => engine.RegisterHandler("slow_step", async (step, state, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // 永遠比 1s 的 timeout 久
            return Array.Empty<CaseEvent>();
        });
}
