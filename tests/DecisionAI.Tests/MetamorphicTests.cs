// ============================================================================
//  Rev2 Phase 1 要求的三條 Metamorphic Relation
//    MR-14  同四元組不同措辭 → canonical id 相同
//    MR-17  把 solver 的原始輸出餵給 critic → 應被拒（context 重建強制）
//    MR-18  情境 payload 不對稱 → L2 應退回（變相的方向暗示）
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Evidence;
using DecisionAI.Modules.Verification;
using DecisionAI.Testing;
using Xunit;

namespace DecisionAI.Tests;

public class MR14_Canonicalization
{
    [Fact]
    public async Task SameFourTuple_DifferentWording_MapsToSameCatalogId()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-MR14", AtsScenario.Request(), AtsScenario.Evidence());

        // solver-A 與 solver-B 對 race_condition 的 Trigger / Observable 措辭完全不同
        var frames = run.Journal.Events.OfType<ClaimProposed>()
            .Where(e => e.Frame.Mechanism == Mechanisms.RaceCondition).ToList();
        Assert.True(frames.Count >= 2, "測試前提：至少兩個 agent 用不同措辭提出同一個機制");
        Assert.True(frames.Select(f => f.Frame.Trigger).Distinct().Count() >= 2, "測試前提：措辭必須真的不同");

        // 合併：同 Mechanism|Locus → 同一個 LocalId
        Assert.Single(frames.Select(f => f.LocalId).Distinct());

        // canonical：同四元組家族 → 同一個 catalog id
        var canon = run.Journal.Events.OfType<ClaimCanonicalized>()
            .Where(e => e.LocalId == frames[0].LocalId).Select(e => e.CatalogId).Distinct().ToList();
        Assert.Single(canon);
        Assert.StartsWith("CLM-", canon[0]);
    }

    [Fact]
    public void Canonicalizer_Exact_Fuzzy_Ambiguous_New()
    {
        var catalog = AtsScenario.SeededCatalog();
        var view = catalog.View(catalog.CurrentVersion);
        var c = new ClaimCanonicalizer();

        var seeded = AtsScenario.RaceFrame with { Trigger = "併發存取", Observable = "執行緒時序重疊" };
        Assert.Equal(MatchKind.Exact, c.Canonicalize(seeded, view, AtsScenario.Domain).Kind);

        var reworded = AtsScenario.RaceFrame with { Trigger = "兩條路徑同時進入", Observable = "時間戳重疊" };
        var fuzzy = c.Canonicalize(reworded, view, AtsScenario.Domain);
        Assert.Equal(MatchKind.Fuzzy, fuzzy.Kind);
        Assert.True(fuzzy.MatchScore is > 0.4 and <= 1.0);

        // 同機制、Locus 部分重疊 → 灰帶，交 arbiter，不自動猜
        var ambiguous = new ClaimFrame(Mechanisms.RaceCondition, "AmqpClient.SendLoop/_link",
                                       "併發存取", "時序重疊");
        Assert.Equal(MatchKind.Ambiguous, c.Canonicalize(ambiguous, view, AtsScenario.Domain).Kind);

        Assert.Equal(MatchKind.New, c.Canonicalize(AtsScenario.PlcFrame, view, AtsScenario.Domain).Kind);
    }

    [Fact]
    public async Task AmbiguousMapping_EscalatesToArbiter()
    {
        var human = new ScriptedHumanGateway(_ => new HumanVerdict(true, "arbiter：同一個故障，併入既有條目"));
        var sys = TestSystem.Build(new TestSystemOptions
        {
            Human = human,
            Agents = () => AtsScenario.Agents().Select(a => (a.Spec, (ILlm)a.Llm)),
            ClaimCatalog = InMemoryClaimCatalog.Seeded(
                (AtsScenario.Domain, new ClaimFrame(Mechanisms.RaceCondition, "AmqpClient.SendLoop/_link", "併發存取", "時序重疊")))
        });
        var run = await sys.Orchestrator.RunAsync("CASE-AMB", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.Contains(human.Requests, r => r.Role == HumanRole.Arbiter);
        Assert.Contains(run.Journal.Events.OfType<HumanActed>(), h => h.HumanRole == nameof(HumanRole.Arbiter));
    }

    [Fact]
    public async Task MutationSwitch_AlwaysNewCanonicalizer_BreaksCrossCaseMemory()
    {
        // 好的 canonicalizer：三條種子都命中 Catalog
        var good = await TestSystem.Build().Orchestrator.RunAsync("CASE-OK14", AtsScenario.Request(), AtsScenario.Evidence());
        Assert.Equal(3, good.State.Claims.Count(k => k.Key.IsCatalogued));

        // 壞的：每一條都變成新條目，跨 case 記憶完全失效
        var sys = TestSystem.Build(new TestSystemOptions { Canonicalizer = new AlwaysNewCanonicalizer() });
        var run = await sys.Orchestrator.RunAsync("CASE-MUT14", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.Equal(0, run.State.Claims.Count(k => k.Key.IsCatalogued));
        Assert.All(run.Journal.Events.OfType<ClaimCanonicalized>(), e =>
        {
            Assert.Equal(MatchKind.New, e.Match);
            Assert.StartsWith("DRAFT-", e.CatalogId);
        });
    }

    [Fact]
    public async Task RewordedFrame_KeepsSameDraftId_WhenNotYetInCatalog()
    {
        // 尚未進 Catalog 的主張也必須穩定：草稿 id 由 Mechanism|Locus 決定，不受措辭影響
        var sys = TestSystem.Build(new TestSystemOptions { ClaimCatalog = InMemoryClaimCatalog.Seeded() });
        var run = await sys.Orchestrator.RunAsync("CASE-DRAFT", AtsScenario.Request(), AtsScenario.Evidence());

        foreach (var g in run.Journal.Events.OfType<ClaimCanonicalized>().GroupBy(e => e.LocalId))
            Assert.Single(g.Select(x => x.CatalogId).Distinct());
        Assert.All(run.State.Claims, k => Assert.StartsWith("DRAFT-", k.Key.Value));
    }
}

public class MR17_ContextRebuild
{
    [Fact]
    public async Task CriticContext_ContainsOnlyProjection_NotSolverRawOutput()
    {
        var sys = TestSystem.Build();
        var run = await sys.Orchestrator.RunAsync("CASE-MR17", AtsScenario.Request(), AtsScenario.Evidence());

        var criticCalls = sys.LlmCalls.Where(c => c.Role == "critic").ToList();
        Assert.NotEmpty(criticCalls);
        var solverOutputs = run.State.AgentRuns.Where(r => r.Role == "solver").Select(r => r.RawOutput).ToList();
        foreach (var call in criticCalls)
            foreach (var raw in solverOutputs)
                Assert.DoesNotContain(raw[..Math.Min(64, raw.Length)], call.User);

        // 投影過的內容本身仍然在（critic 看得到主張與證據）
        Assert.All(criticCalls, c => Assert.Contains("H1:", c.User));
    }

    [Fact]
    public async Task FeedingSolverRawOutputToCritic_IsRejected()
    {
        // 壞的 context builder：把 solver 的原始輸出直接拼進 critic 的 prompt
        var sys = TestSystem.Build(new TestSystemOptions
        {
            CriticContext = (s, guard) =>
                CriticContextBuilder.Build(s, guard) + "\n\nsolver 原始輸出：\n" +
                string.Join("\n", s.AgentRuns.Where(r => r.Role == "solver").Select(r => r.RawOutput))
        });
        var run = await sys.Orchestrator.RunAsync("CASE-MR17B", AtsScenario.Request(), AtsScenario.Evidence());

        // 守衛在 critic 這一步丟例外 → 該步驟失敗 → 流程 fail-closed 中止
        Assert.True(run.State.Halted);
        Assert.Contains(run.State.Log, l => l.Contains("context 重建"));
        GoldenTests.AssertAbstained(run.Report, ReasonCode.PipelineHalted);
    }
}

public class MR18_ScenarioSymmetry
{
    private static readonly IEvidenceStore Store = BuildStore();

    private static IEvidenceStore BuildStore()
    {
        var s = new InMemoryEvidenceStore(new FixedClock());
        for (int i = 0; i < 6; i++) s.Admit(new EvidenceDraft(EvidenceSource.RuntimeLog, "log", $"觀察 {i}"));
        return s;
    }

    private static ScenarioBriefing Briefing(int evA, int lenA, int evB, int lenB)
    {
        Scenario Make(string name, int ev, int len) => new(name,
            ImmutableArray.Create(new FactLine(new string('字', len),
                Enumerable.Range(1, ev).Select(i => $"EV-{i:000}").ToImmutableArray())),
            ImmutableArray.Create(new LeadingIndicator("指標", "syslog", "> 0 次", "每班")));

        return new ScenarioBriefing(
            ImmutableArray.Create(new FactLine("背景事實", ImmutableArray.Create("EV-001"))),
            ImmutableArray.Create(Make("樂觀", evA, lenA), Make("悲觀", evB, lenB)),
            new RiskExit("曝險", "退出條件"));
    }

    [Fact]
    public void SymmetricBriefing_Passes()
    {
        var v = new PayloadVerifier().Verify(Briefing(2, 100, 2, 90), ReasonCode.ReflexiveDirection, Store);
        Assert.True(v.Ok, string.Join("；", v.Failures));
    }

    [Fact]
    public void AsymmetricEvidence_IsRejected()
    {
        var v = new PayloadVerifier().Verify(Briefing(5, 100, 1, 100), ReasonCode.ReflexiveDirection, Store);
        Assert.False(v.Ok);
        Assert.Contains(v.Failures, f => f.Contains("方向暗示"));
    }

    [Fact]
    public void AsymmetricLength_IsRejected()
    {
        var v = new PayloadVerifier().Verify(Briefing(2, 500, 2, 50), ReasonCode.ReflexiveDirection, Store);
        Assert.False(v.Ok);
        Assert.Contains(v.Failures, f => f.Contains("篇幅"));
    }

    [Fact]
    public void UnobservableIndicator_IsRejected()
    {
        var bad = Briefing(2, 100, 2, 100);
        bad = bad with
        {
            Scenarios = bad.Scenarios.SetItem(0, bad.Scenarios[0] with
            {
                Indicators = ImmutableArray.Create(new LeadingIndicator("密切關注市場動向", "", "", ""))
            })
        };
        var v = new PayloadVerifier().Verify(bad, ReasonCode.ReflexiveDirection, Store);
        Assert.False(v.Ok);
        Assert.Contains(v.Failures, f => f.Contains("不可觀察"));
    }

    [Fact]
    public void PayloadTypeMismatch_IsRejectedAtCompileTimeContract()
    {
        // ConsiderationMap 不得掛在 ReflexiveDirection 上：型別自己宣告了適用範圍
        var map = new ConsiderationMap(
            ImmutableArray.Create(new Consideration("A", "誰", ImmutableArray<string>.Empty),
                                  new Consideration("B", "誰", ImmutableArray<string>.Empty)),
            ImmutableArray<Conflict>.Empty,
            ImmutableArray.Create(new OpenQuestion("誰決定？", "人")));

        var v = new PayloadVerifier().Verify(map, ReasonCode.ReflexiveDirection, Store);
        Assert.False(v.Ok);
        Assert.Contains(v.Failures, f => f.Contains("型別錯配"));
        Assert.True(new PayloadVerifier().Verify(map, ReasonCode.NoGroundTruth, Store).Ok);
    }

    [Fact]
    public void MutationSwitch_PermissiveVerifier_LetsAsymmetryThrough()
    {
        var v = new PermissivePayloadVerifier().Verify(Briefing(5, 500, 1, 50), ReasonCode.ReflexiveDirection, Store);
        Assert.True(v.Ok, "壞的守門件會放行明顯不對稱的情境 —— MR-18 因此變紅");
    }
}
