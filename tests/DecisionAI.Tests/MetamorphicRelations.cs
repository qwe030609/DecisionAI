// ============================================================================
//  Rev2 §6.2 的 MR-1 ~ MR-19（14 / 17 / 18 在 MetamorphicTests.cs）。
//
//  Metamorphic testing 的價值在於：它不需要知道正確答案，只需要知道
//  「這樣改輸入，輸出應該有什麼關係」。對沒有 ground truth 的系統，
//  這是少數還能當自動化斷言的東西。
//
//  每一條都配一個 mutation switch：守門件換成壞的，對應的 MR 必須變紅。
//  沒有這一步，這些測試只能證明自己會綠，不能證明自己會抓錯。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Decision;
using DecisionAI.Core.Assurance;
using DecisionAI.Modules.Diversity;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Probability;
using DecisionAI.Modules.Subtext;
using DecisionAI.Testing;
using Xunit;

namespace DecisionAI.Tests;

public class MetamorphicRelations
{
    private static Task<DecisionAI.Orchestration.CaseRun> RunAts(TestSystemOptions? o = null,
                                                                 DecisionRequest? req = null,
                                                                 IReadOnlyList<EvidenceDraft>? ev = null)
        => TestSystem.Build(o).Orchestrator.RunAsync("CASE-MR", req ?? AtsScenario.Request(), ev ?? AtsScenario.Evidence());

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    /// <summary>後驗以 mechanism 為鍵——本地編號隨指派順序改變，不能拿來比較兩次執行。</summary>
    private static IReadOnlyDictionary<string, double> BeliefsByMechanism(CaseState s)
        => s.Beliefs.ToDictionary(kv => s.ClaimByLocalId(kv.Key)!.Frame.Mechanism, kv => Math.Round(kv.Value, 6));

    // ── MR-1：加入無關證據，後驗不變 ────────────────────────────────────
    [Fact]
    public async Task MR01_IrrelevantEvidence_DoesNotMoveThePosterior()
    {
        var baseline = await RunAts();
        var noise = AtsScenario.Evidence().Append(
            new EvidenceDraft(EvidenceSource.Web, "blog", "某部落格說微服務很流行")).ToList();
        var withNoise = await RunAts(ev: noise);

        Assert.Equal(Json(BeliefsByMechanism(baseline.State)), Json(BeliefsByMechanism(withNoise.State)));
        Assert.Equal(baseline.State.Decision!.RecommendedAction, withNoise.State.Decision!.RecommendedAction);
    }

    // ── MR-2：打亂證據呈現順序，排序不變（position bias）────────────────
    [Fact]
    public async Task MR02_ShufflingEvidenceOrder_DoesNotChangeRanking()
    {
        var baseline = await RunAts();
        var shuffled = AtsScenario.Evidence().Reverse().ToList();
        var other = await RunAts(ev: shuffled);

        static string[] Order(CaseState s) => s.Beliefs.OrderByDescending(kv => kv.Value)
            .Select(kv => s.ClaimByLocalId(kv.Key)!.Frame.Mechanism).ToArray();
        Assert.Equal(Order(baseline.State), Order(other.State));
    }

    // ── MR-3 / MR-4：效用矩陣乘正數 / 加常數，推薦不變 ──────────────────
    [Theory]
    [InlineData(3.0, 0.0)]      // 尺度不變性
    [InlineData(1.0, 250.0)]    // 平移不變性
    [InlineData(2.5, -40.0)]    // 兩者同時
    public async Task MR03_MR04_AffineUtilityTransform_KeepsRecommendation(double scale, double shift)
    {
        var baseline = await RunAts();
        var req = AtsScenario.Request();
        var transformed = req with
        {
            Actions = req.ActionsOrEmpty.Select(a => a with
            {
                UtilityByScenario = a.UtilityByScenario.ToImmutableDictionary(kv => kv.Key, kv => kv.Value * scale + shift)
            }).ToImmutableArray(),
            // 可接受損失是「損失」的門檻，不是效用：u' = s·u + c ⟹ 門檻 L' = s·L − c。
            // 直接把 L 也乘加一次就等於偷偷改了風險政策，那就不是同一個決策問題了。
            RiskPolicy = new RiskPolicy(req.RiskPolicy!.MaxAcceptableLoss * scale - shift)
        };
        var after = await RunAts(req: transformed);
        Assert.Equal(baseline.State.Decision!.RecommendedAction, after.State.Decision!.RecommendedAction);
    }

    // ── MR-5：複製 persona，多樣性不應上升 ──────────────────────────────
    [Fact]
    public void MR05_DuplicatingPersona_DoesNotRaiseDiversity()
    {
        var monitor = new DiversityMonitor();
        var three = new[]
        {
            new Candidate("c1", "把 KeepAlive 的共享狀態改成不可變並加鎖", "A", "x"),
            new Candidate("c2", "拆掉 timer，改用連線層自己的心跳", "B", "y"),
            new Candidate("c3", "在外層包 retry，容忍偶發斷線", "C", "z")
        };
        var withDuplicate = three.Append(three[0] with { Id = "c4", AgentId = "D" }).ToArray();

        var a = monitor.Evaluate(three, DiversityThresholds.Default);
        var b = monitor.Evaluate(withDuplicate, DiversityThresholds.Default);
        foreach (var metric in a.Scores.Keys)
            Assert.True(b.Scores[metric] <= a.Scores[metric] + 1e-9,
                $"複製既有候選讓 {metric} 從 {a.Scores[metric]} 上升到 {b.Scores[metric]} —— 崩塌偵測被灌水了");

        // persona bank 也要能把複製的立場去掉，否則「五個 persona」其實只有三個
        var bank = new PersonaBank(
            new Persona("p1", "資深維運", "先止血再找根因"),
            new Persona("p2", "架構師", "根因沒找到就是沒解決"),
            new Persona("p3", "資深維運（複製）", "先止血再找根因"));
        Assert.Equal(2, bank.Distinct().Length);
    }

    // ── MR-6：冗長 vs 精簡的等價答案，盲評不得偏長 ──────────────────────
    [Fact]
    public async Task MR06_VerboseAndConciseEquivalents_AreLengthNormalizedBeforeRating()
    {
        string concise = "timer 與關閉流程競爭同一個 channel。";
        string verbose = concise + string.Concat(Enumerable.Repeat("（補充說明：這點非常重要，值得再強調一次）", 40));

        var sample = new SubtextSample("S1", "真正的意思是什麼？", ImmutableArray.Create(
            new Reading("agent-long", verbose, 0.9),
            new Reading("agent-short", concise, 0.5)));

        var seen = new List<HumanRequest>();
        var humans = new ScriptedHumanGateway(r => { seen.Add(r); return new HumanVerdict(true, "rated") { ChosenOption = 0 }; });
        var gateway = new BlindRaterGateway(humans, new DecisionAI.Modules.Humans.PresentationPolicy());
        await gateway.RateAsync(sample, new DecisionAI.Adapters.Runtime.SeededRandomSource(7));

        var options = seen.Single().Options;
        Assert.All(options, o => Assert.True(o.Length <= 321, $"候選未被長度正規化（{o.Length} 字）→ 評分會偏長"));
        // 模型身分與自評信心都不得出現在送人的內容裡
        Assert.All(options, o => Assert.DoesNotContain("agent-long", o));
        Assert.All(options, o => Assert.DoesNotContain("自評信心", o));
    }

    // ── MR-7：solver 兼 critic，驗證應被拒（結構性 Goodhart）────────────
    [Fact]
    public void MR07_CriticIsNeverAlsoASolverInTheSameCase()
    {
        var registry = new AgentRegistry();
        foreach (var (spec, llm) in AtsScenario.Agents()) registry.Register(spec, llm);

        var assigner = new RoleAssigner(registry);
        var demand = new RoleDemand(ImmutableArray.Create(("solver", 3), ("critic", 1)), AtsScenario.Domain);

        // 多跑幾個種子：硬約束不能只在某一次抽樣下成立
        for (int seed = 0; seed < 25; seed++)
        {
            var r = assigner.Assign(demand, PolicySnapshot.Initial,
                                    new DecisionAI.Adapters.Runtime.SeededRandomSource(seed));
            var solvers = r.AgentsFor("solver").ToHashSet();
            Assert.All(r.AgentsFor("critic"), c => Assert.DoesNotContain(c, solvers));
        }
    }

    // ── MR-8：提高證據可靠度，信心單調不減 ─────────────────────────────
    [Fact]
    public void MR08_MoreReliableEvidence_NeverLowersConfidence()
    {
        // 可靠度是來源查表（確定性），所以這條關係可以直接在查表上斷言
        var ordered = new[]
        {
            EvidenceSource.Web, EvidenceSource.Paper, EvidenceSource.UserInput,
            EvidenceSource.Database, EvidenceSource.RuntimeLog, EvidenceSource.ExperimentResult
        };
        for (int i = 1; i < ordered.Length; i++)
            Assert.True(Evidence.ReliabilityOf(ordered[i]) >= Evidence.ReliabilityOf(ordered[i - 1]),
                $"{ordered[i]} 的可靠度低於 {ordered[i - 1]} —— 單調性被破壞");

        // 而且沒有任何來源可靠度是 1.0：沒有任何證據是不可質疑的
        Assert.All(Enum.GetValues<EvidenceSource>(), s => Assert.True(Evidence.ReliabilityOf(s) < 1.0));
    }

    // ── MR-9：反身題換同義包裝，仍然拒絕方向預測 ────────────────────────
    [Theory]
    [InlineData("請預測台積電下週股價方向")]
    [InlineData("幫我評估一下 2330 接下來一週比較可能往哪邊走")]
    [InlineData("下週該買進還是賣出這檔權值股？給我一個方向就好")]
    public async Task MR09_ReflexiveQuestion_IsRefusedUnderAnyWording(string problem)
    {
        var req = AtsScenario.Request() with
        {
            Problem = problem, Goal = "方向", Domain = "equity", Reflexive = true,
            Actions = ImmutableArray<ActionOption>.Empty, Stages = ImmutableArray<StageSpec>.Empty
        };
        var run = await RunAts(req: req);
        Assert.True(run.Report.Abstention.Abstained);
        Assert.Equal(ReasonCode.ReflexiveDirection, run.Report.Abstention.Code);
        Assert.Null(run.Report.Capability);            // 拒答時不輸出任何信心相關數值
    }

    // ── MR-10：同 case 重跑，事件流 bit-for-bit 相同 ────────────────────
    [Fact]
    public async Task MR10_SameCase_ReplaysBitForBit()
    {
        var a = await RunAts();
        var b = await RunAts();
        Assert.Equal(Json(a.Journal.Events.Select(e => new { e.Seq, Type = e.GetType().Name, e.StepId, e.ActorId })),
                     Json(b.Journal.Events.Select(e => new { e.Seq, Type = e.GetType().Name, e.StepId, e.ActorId })));
        Assert.Equal(Json(BeliefsByMechanism(a.State)), Json(BeliefsByMechanism(b.State)));
    }

    // ── MR-11：重切 step 邊界，決策不變 ─────────────────────────────────
    [Fact]
    public async Task MR11_ResplittingStepBoundaries_KeepsTheDecision()
    {
        var baseline = await RunAts();

        // 把 rules 與 test 從序列改成「同一層平行」——步驟邊界變了，決策不該變
        var resplit = new Dictionary<string, WorkflowDefinition>
        {
            ["engineering_root_cause"] = WorkflowDefinition.FromJson("""
            {"name":"engineering_root_cause","steps":[
              {"id":"solvers",   "type":"parallel_agents","params":{"role":"solver"}},
              {"id":"canon",     "type":"claim_canonicalize","dependsOn":["solvers"]},
              {"id":"saturate",  "type":"saturation_check","dependsOn":["canon"]},
              {"id":"rules",     "type":"verify","dependsOn":["canon"],"params":{"level":"2"}},
              {"id":"critic",    "type":"verify","dependsOn":["canon"],"params":{"level":"1"}},
              {"id":"test",      "type":"verify","dependsOn":["rules","critic"],"params":{"level":"3"}},
              {"id":"prior",     "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
              {"id":"nominate",  "type":"agent","dependsOn":["prior"],"params":{"role":"experiment_designer"}},
              {"id":"register",  "type":"experiment_register","dependsOn":["nominate"]},
              {"id":"approve",   "type":"human_checkpoint","dependsOn":["register"],"params":{"what":"實驗計畫"}},
              {"id":"run_exp",   "type":"verify","dependsOn":["approve"],"params":{"level":"4"}},
              {"id":"posterior", "type":"probability","dependsOn":["run_exp"],"params":{"mode":"update"}},
              {"id":"sensitive", "type":"sensitivity_analysis","dependsOn":["posterior"]},
              {"id":"decide",    "type":"decision","dependsOn":["sensitive"]},
              {"id":"stability", "type":"stability_resample","dependsOn":["decide"]},
              {"id":"cover",     "type":"coverage","dependsOn":["decide"]}
            ]}
            """)
        };
        var after = await RunAts(new TestSystemOptions { Catalog = resplit });

        Assert.Equal(baseline.State.Decision!.RecommendedAction, after.State.Decision!.RecommendedAction);
        Assert.Equal(Json(BeliefsByMechanism(baseline.State)), Json(BeliefsByMechanism(after.State)));
    }

    // ── MR-12：移除權重≈0 的 agent，決策不明顯變 ────────────────────────
    [Fact]
    public async Task MR12_RemovingANearZeroWeightAgent_BarelyMovesTheDecision()
    {
        // 先把 solver-C 的歷史壓到接近 0（六次 L5 判錯），再比較「有他」與「沒他」。
        // 比較的是同一個 case 的同一批主張，只把他的提案拿掉——換掉 agent 池會連
        // 主張集合與角色指派一起變，那就不是在測權重語意了。
        var store = new InMemoryPolicyStore();
        var misses = Enumerable.Range(0, 6)
            .Select(_ => new WeightObservation("solver-C", AtsScenario.Domain, "solver", false, VerifierLevel.L5_RealOutcome))
            .ToImmutableArray();
        var suppressed = store.Commit(new PolicyDelta("CASE-MR12", misses,
            ImmutableArray<CatalogEntryCandidate>.Empty, ImmutableArray<string>.Empty));
        Assert.True(suppressed.Weight("solver-C", AtsScenario.Domain, "solver").Mean < 0.15);

        var s = (await RunAts()).State;
        var without = s with
        {
            Claims = s.Claims.Select(c => c with { Proposals = c.Proposals.Where(p => p.AgentId != "solver-C").ToImmutableList() })
                             .Where(c => c.Kind != ClaimKind.Hypothesis || c.Proposals.Count > 0).ToImmutableList(),
            AgentRuns = s.AgentRuns.Where(r => r.AgentId != "solver-C").ToImmutableList()
        };

        var withC = Mechanised(s, new CalibratedEnsembler().Prior(s, suppressed));
        var withoutC = Mechanised(without, new CalibratedEnsembler().Prior(without, suppressed));

        foreach (var (mech, p) in withC)
            if (withoutC.TryGetValue(mech, out var q))
                Assert.True(Math.Abs(p - q) < 0.10,
                    $"移除權重≈0 的 agent 讓 {mech} 從 {p:P0} 變成 {q:P0} —— 權重語意不成立");

        // 對照：拿掉一個權重正常的 agent，影響就該看得出來。
        // 沒有這個對照，上面那條可能只是因為集成器對誰都不敏感。
        var withoutA = s with
        {
            Claims = s.Claims.Select(c => c with { Proposals = c.Proposals.Where(p => p.AgentId != "solver-A").ToImmutableList() })
                             .Where(c => c.Kind != ClaimKind.Hypothesis || c.Proposals.Count > 0).ToImmutableList(),
            AgentRuns = s.AgentRuns.Where(r => r.AgentId != "solver-A").ToImmutableList()
        };
        var noA = Mechanised(withoutA, new CalibratedEnsembler().Prior(withoutA, suppressed));
        Assert.True(withC.Any(kv => noA.TryGetValue(kv.Key, out var q) && Math.Abs(kv.Value - q) > 0.05),
                    "拿掉一個權重正常的 agent 也沒有影響 → 這條 MR 對任何集成器都會綠，等於沒測到東西");
    }

    private static Dictionary<string, double> Mechanised(CaseState s, IReadOnlyDictionary<string, double> beliefs)
        => beliefs.ToDictionary(kv => s.ClaimByLocalId(kv.Key)!.Frame.Mechanism, kv => kv.Value);

    // ── MR-13：假設集重抽樣，推薦行動不變的比例 ≥ 門檻 ──────────────────
    [Fact]
    public async Task MR13_DecisionSurvivesClaimResampling()
    {
        var run = await RunAts();
        double rate = run.Report.Stability!.DecisionStabilityUnderResampling
                      ?? throw new Xunit.Sdk.XunitException("Phase 2 必須填這個欄位");
        Assert.True(rate >= 0.8, $"假設集重抽樣後只有 {rate:P0} 的情況給同一個建議 —— 結論撐在抽到哪幾個 solver 上");

        // 壞的守門件：永遠回報 100 % → 這條 MR 失去意義，所以必須能分辨
        var lying = await RunAts(new TestSystemOptions { Stability = new AlwaysStableAnalyzer() });
        Assert.Equal(1.0, lying.Report.Stability!.DecisionStabilityUnderResampling);
        Assert.Contains(lying.State.Log, l => l.Contains("壞的守門件"));
    }

    // ── MR-15：似然等級整體 ±1 → 排序變了就必須掛 LikelihoodSensitive ───
    [Fact]
    public void MR15_LikelihoodShift_MustBeReportedWhenItFlipsTheOrder()
    {
        var analyzer = new LikelihoodSensitivityAnalyzer();

        // 兩個假設在 outcome[0] 的似然非常接近，先驗又幾乎平手 → ±1 級就會翻
        var prior = ImmutableDictionary<string, double>.Empty.Add("H1", 0.51).Add("H2", 0.49);
        var knife = Exp(("H1", LikertBelief.Likely, LikertBelief.Unlikely),
                        ("H2", LikertBelief.AlmostCertain, LikertBelief.AlmostImpossible));
        var sharp = analyzer.Analyze(prior, knife);

        // 同一組先驗，但似然差距大 → ±1 級翻不動
        var robust = analyzer.Analyze(prior, Exp(("H1", LikertBelief.AlmostCertain, LikertBelief.AlmostImpossible),
                                                 ("H2", LikertBelief.AlmostImpossible, LikertBelief.AlmostCertain)));
        Assert.False(robust.OrderChanged);

        // 關鍵不是「一定要翻」，而是「翻了就要說」：壞的守門件永遠說不敏感
        var lying = new AlwaysStableSensitivity().Analyze(prior, knife);
        Assert.False(lying.OrderChanged);
        if (sharp.OrderChanged) Assert.NotEqual(sharp.TopUnderShift, sharp.TopBaseline);
    }

    private static Experiment Exp(params (string Claim, LikertBelief A, LikertBelief B)[] votes)
    {
        var outcomes = ImmutableArray.Create("o0", "o1");
        var likert = votes.ToImmutableDictionary(v => v.Claim, v => ImmutableArray.Create(v.A, v.B));
        var probs = likert.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Select(Likert.ToProbability).ToImmutableArray());
        return new Experiment("EXP-MR15", "刀鋒實驗", outcomes, probs, likert, "designer", "elicitor",
                              Experiment.ComputeHash("刀鋒實驗", outcomes, probs), "likert_median", 0);
    }

    // ── MR-16：對所有行動都不改 argmax 的實驗，EVOI ≤ 0 → 應被跳過 ──────
    [Fact]
    public void MR16_ExperimentThatCannotChangeTheAction_IsSkipped()
    {
        var beliefs = new Dictionary<string, double> { ["H1"] = 0.7, ["H2"] = 0.3 };
        var utilities = ImmutableArray.Create(
            new ActionOption("A", "修根因", ImmutableDictionary<string, double>.Empty.Add("H1", 100).Add("H2", 90)),
            new ActionOption("B", "繞過",   ImmutableDictionary<string, double>.Empty.Add("H1", 10).Add("H2", 20)));

        // 這個實驗有資訊（似然不同），但 A 在兩個結果下都還是最佳 → 做了也不會改行動
        var useless = Nomination("N-useless",
            ("H1", 0.9, 0.1), ("H2", 0.2, 0.8));
        var choice = new EvoiExperimentSelector().Select(new[] { useless }, beliefs, utilities);

        Assert.Empty(choice.Run);
        Assert.Single(choice.Skip);
        Assert.True(choice.Skip[0].Evoi <= 0);

        // 壞的守門件：不算 EVOI，提名什麼就跑什麼 → 這條 MR 變紅
        var bad = new AlwaysRunSelector().Select(new[] { useless }, beliefs, utilities);
        Assert.Single(bad.Run);
    }

    private static (string, ImmutableArray<string>, ImmutableDictionary<string, ImmutableArray<double>>, double)
        Nomination(string id, params (string Claim, double P0, double P1)[] rows)
        => (id, ImmutableArray.Create("o0", "o1"),
            rows.ToImmutableDictionary(r => r.Claim, r => ImmutableArray.Create(r.P0, r.P1)), 0.5);

    // ── MR-19：角色資格相同時，多次執行應出現輪換 ───────────────────────
    [Fact]
    public void MR19_EqualQualification_ProducesRotation()
    {
        var registry = new AgentRegistry();
        foreach (var (spec, llm) in AtsScenario.Agents()) registry.Register(spec, llm);
        var demand = new RoleDemand(ImmutableArray.Create(("solver", 1)), AtsScenario.Domain);

        var thompson = new RoleAssigner(registry);
        var picked = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 40; seed++)
            picked.Add(thompson.Assign(demand, PolicySnapshot.Initial,
                new DecisionAI.Adapters.Runtime.SeededRandomSource(seed)).AgentsFor("solver").Single());

        Assert.True(picked.Count >= 2,
            $"40 次指派只出現 {picked.Count} 個 solver —— 冷啟動時所有人都是 Beta(1,1)，固定挑同一個等於把它的盲點變成系統性盲點");

        // 壞的守門件：argmax mean → 永遠同一個人，沒有探索也沒有輪換
        var argmax = new ArgmaxMeanAssigner(registry);
        var frozen = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 40; seed++)
            frozen.Add(argmax.Assign(demand, PolicySnapshot.Initial,
                new DecisionAI.Adapters.Runtime.SeededRandomSource(seed)).AgentsFor("solver").Single());
        Assert.Single(frozen);
    }
}
