// ============================================================================
//  Phase 2 各守門件的性質測試。每一組都成對出現：
//    · 好的守門件在該擋的時候擋、該說的時候說
//    · 換成 broken 版本後，同一條斷言必須失效
//  只有後者能證明前者不是恆真式。
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
using DecisionAI.Modules.Diversity;
using DecisionAI.Modules.Humans;
using DecisionAI.Modules.Probability;
using DecisionAI.Modules.Subtext;
using DecisionAI.Testing;
using Xunit;

namespace DecisionAI.Tests;

// ── Conformal 覆蓋層 ────────────────────────────────────────────────────
public class ConformalTests
{
    private static PolicySnapshot WithQuantile(string family, double threshold, int n)
        => PolicySnapshot.Initial with
        {
            Conformal = ImmutableDictionary<string, ConformalQuantile>.Empty
                .Add(family, new ConformalQuantile(0.10, threshold, n, 20))
        };

    [Theory]
    [InlineData(TaskFamilies.Reflexive)]
    [InlineData(TaskFamilies.Drifting)]
    public void ReflexiveAndDriftingFamilies_NeverGetACoverageGuarantee(string family)
    {
        var c = new ConformalCalibrator();
        Assert.False(c.IsApplicable(family));
        // 即使校準表裡硬塞了分位數，也不給——可交換性被破壞時的保證是假保證
        var beliefs = new Dictionary<string, double> { ["H1"] = 0.8, ["H2"] = 0.2 };
        Assert.Null(c.Predict(beliefs, family, WithQuantile(family, 0.5, 100)));
    }

    [Fact]
    public void TooFewCalibrationSamples_MeansNoCoverageLayer()
    {
        var c = new ConformalCalibrator();
        var beliefs = new Dictionary<string, double> { ["H1"] = 0.8, ["H2"] = 0.2 };
        Assert.Null(c.Predict(beliefs, TaskFamilies.Diagnosis, WithQuantile(TaskFamilies.Diagnosis, 0.5, 9)));
        Assert.NotNull(c.Predict(beliefs, TaskFamilies.Diagnosis, WithQuantile(TaskFamilies.Diagnosis, 0.5, 10)));
    }

    [Fact]
    public void PredictionSet_IsNeverEmpty_AndGrowsWithTheThreshold()
    {
        var c = new ConformalCalibrator();
        var beliefs = new Dictionary<string, double> { ["H1"] = 0.55, ["H2"] = 0.30, ["H3"] = 0.15 };
        var tight = c.Predict(beliefs, TaskFamilies.Diagnosis, WithQuantile(TaskFamilies.Diagnosis, 0.20, 50))!;
        var loose = c.Predict(beliefs, TaskFamilies.Diagnosis, WithQuantile(TaskFamilies.Diagnosis, 0.90, 50))!;
        Assert.NotEmpty(tight.PredictionSet);
        Assert.True(loose.PredictionSet.Length >= tight.PredictionSet.Length);
    }

    [Fact]
    public void EmpiricalCoverage_IsMeasuredOnTheAuditSetOnly()
    {
        // 校準集刻意全部是「真值機率很高」的簡單樣本，稽核集才是隨機的難樣本。
        // 若把校準集混進來量覆蓋率，數字會被自己挑的樣本灌水。
        var samples = new List<ConformalSample>();
        for (int i = 0; i < 20; i++) samples.Add(Sample(0.95, isAudit: false));
        for (int i = 0; i < 10; i++) samples.Add(Sample(0.40, isAudit: true));

        var q = ConformalCalibrationBuilder.Build(samples);
        Assert.Equal(20, q.CalibrationSize);
        Assert.Equal(10, q.AuditSize);

        double coverage = ConformalCalibrationBuilder.EmpiricalCoverage(samples, q);
        Assert.Equal(0.0, coverage);        // 稽核集的難樣本全部落在預測集合外 → 誠實地回報 0
    }

    [Fact]
    public void MutationSwitch_AlwaysApplicable_ClaimsGuaranteesForReflexiveTasks()
    {
        var bad = new AlwaysApplicableConformal();
        Assert.True(bad.IsApplicable(TaskFamilies.Reflexive));
        Assert.NotNull(bad.Predict(new Dictionary<string, double> { ["H1"] = 0.5 }, TaskFamilies.Reflexive, PolicySnapshot.Initial));
    }

    [Theory]
    [InlineData(true, GroundTruthStatus.Agreed, VerifierLevel.L3_ExecutableTest, TaskFamilies.Reflexive)]
    [InlineData(false, GroundTruthStatus.Contested, VerifierLevel.L3_ExecutableTest, TaskFamilies.Drifting)]
    [InlineData(false, GroundTruthStatus.Agreed, VerifierLevel.L3_ExecutableTest, TaskFamilies.Diagnosis)]
    [InlineData(false, GroundTruthStatus.Agreed, VerifierLevel.L2_Rule, TaskFamilies.Classification)]
    [InlineData(false, GroundTruthStatus.Agreed, VerifierLevel.L1_LlmCritic, TaskFamilies.InformationProcessing)]
    public void TaskFamily_IsDerivedFromFacts_NotFromTheModel(bool reflexive, GroundTruthStatus gt,
                                                              VerifierLevel level, string expected)
    {
        var facts = new ProblemFacts(level, reflexive, false, true, 0, "d", RiskLevel.Medium, gt);
        Assert.Equal(expected, TaskFamilyClassifier.Classify(facts));
    }

    private static ConformalSample Sample(double pForTruth, bool isAudit)
        => new(TaskFamilies.Diagnosis,
               ImmutableDictionary<string, double>.Empty.Add("H1", pForTruth).Add("H2", 1 - pForTruth),
               "H1", isAudit);
}

// ── Chao1 飽和度 ────────────────────────────────────────────────────────
public class SaturationTests
{
    [Fact]
    public void ManySingletons_MeanLowCoverage()
    {
        // 四條假設各只有一個 agent 提過 → 假設空間顯然還沒找完
        var lonely = State(1, 1, 1, 1);
        var shared = State(3, 3, 2, 2);
        var est = new Chao1SaturationEstimator();

        var a = est.Estimate(lonely, 3);
        var b = est.Estimate(shared, 3);
        Assert.True(Chao1SaturationEstimator.Coverage(4, a) < Chao1SaturationEstimator.Coverage(4, b),
            "singletons 多的時候覆蓋率必須更低，否則飽和停止準則沒有意義");
        Assert.Equal(4, a.Singletons);
        Assert.Equal(0, b.Singletons);
    }

    [Fact]
    public void NoSingletons_MeansFullCoverage()
    {
        var est = new Chao1SaturationEstimator().Estimate(State(3, 3), 3);
        Assert.Equal(0, est.Singletons);
        Assert.Equal(0, est.EstimatedUndiscovered);
        Assert.Equal(1.0, Chao1SaturationEstimator.Coverage(2, est));
    }

    /// <summary>proposerCounts[i] = 第 i 條假設被幾個不同 agent 提過。</summary>
    private static CaseState State(params int[] proposerCounts)
    {
        var claims = proposerCounts.Select((n, i) => new Claim(
            $"H{i + 1}", new ClaimKey($"K{i}"), ClaimKind.Hypothesis,
            AtsScenario.RaceFrame with { Locus = $"locus-{i}" },
            Enumerable.Range(0, n).Select(k => new Proposal($"agent-{k}", LikertBelief.Likely)).ToImmutableList(),
            ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty, false, MatchKind.Exact, 1)).ToImmutableList();
        return CaseState.Empty("CASE-SAT") with { Claims = claims };
    }
}

// ── 校準與錯誤相關性 ───────────────────────────────────────────────────
public class CalibrationAndCorrelationTests
{
    [Fact]
    public void OverconfidentAgent_GetsShrunkTowardOneHalf()
    {
        // 十次都說 90% 但只對了三次 → 顯著過度自信
        var records = Enumerable.Range(0, 10).Select(i => (0.9, i < 3)).ToImmutableArray();
        var policy = PolicySnapshot.Initial with
        {
            Calibration = ImmutableDictionary<string, CalibrationCurve>.Empty.Add("loud", new CalibrationCurve(records))
        };

        double shrunk = new CalibrationEngine().Recalibrate("loud", 0.9, policy);
        Assert.True(shrunk < 0.9, "過度自信的 agent 沒有被收縮");
        Assert.True(shrunk > 0.5, "收縮不應該反轉方向");

        // 壞的守門件：不校準 → 自報 0.9 就是 0.9
        Assert.Equal(0.9, new NoOpCalibrator().Recalibrate("loud", 0.9, policy), 3);
    }

    [Fact]
    public void CalibrationCurve_ReportsBrierAndEce()
    {
        var curve = new CalibrationCurve(ImmutableArray.Create((0.9, true), (0.9, false), (0.1, false), (0.8, true)));
        Assert.NotNull(curve.Brier);
        Assert.NotNull(curve.Ece());
        Assert.InRange(curve.Brier!.Value, 0, 1);
    }

    [Fact]
    public void SameFamilyAgents_AreTreatedAsMoreCorrelatedThanUnrelatedOnes()
    {
        var ctx = new CorrelationContext(
            ImmutableDictionary<string, AgentLineage>.Empty
                .Add("a", new AgentLineage("vendor-x", "x-large", "g1"))
                .Add("b", new AgentLineage("vendor-x", "x-large", "g2"))
                .Add("c", new AgentLineage("vendor-x", "x-small", "g1"))
                .Add("d", new AgentLineage("vendor-y", "y-large", "g1")),
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty,
            PolicySnapshot.Initial);

        var e = new LineagePriorEstimator();
        double sameFamily = e.Estimate("a", "b", ctx).Item1;
        double sameVendor = e.Estimate("a", "c", ctx).Item1;
        double unrelated = e.Estimate("a", "d", ctx).Item1;
        Assert.True(sameFamily > sameVendor && sameVendor > unrelated);

        // 壞的守門件：宣稱完全獨立 → 同 family 的一致答案被當成多份獨立證據
        Assert.Equal(0, new ZeroCorrelation().Estimate("a", "b", ctx).Item1);
    }

    [Fact]
    public void IdenticalClaimSets_CanOnlyRaiseTheCorrelationEstimate_NeverLowerIt()
    {
        // instance 層看到兩個 agent 提了完全一樣的主張，只能把相關性往上修，
        // 不能因此宣稱「其實他們比 lineage 以為的更獨立」。
        var lineage = ImmutableDictionary<string, AgentLineage>.Empty
            .Add("a", new AgentLineage("vendor-x", "x-large", "g1"))
            .Add("b", new AgentLineage("vendor-y", "y-large", "g1"));      // 不同 family → 先驗較低
        var same = ImmutableHashSet.Create("K1", "K2");
        var ctx = new CorrelationContext(lineage,
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.Add("a", same).Add("b", same),
            PolicySnapshot.Initial);

        var blended = new BlendedCorrelation();
        double withIdentical = blended.Estimate("a", "b", ctx).Rho;
        double baseline = new LineagePriorEstimator().Estimate("a", "b", ctx).Item1;
        Assert.True(withIdentical >= baseline);
    }
}

// ── 反 automation bias 的呈現政策 ───────────────────────────────────────
public class PresentationPolicyTests
{
    private static HumanRequest Raw(HumanRole role) => new("CASE-P", "step", role, "要不要照這個做？",
        ImmutableArray.Create("系統建議：部署 A", "證據 5 條", "最壞情況 -30"))
    { SystemRecommendation = "部署 A" };

    [Fact]
    public void Approver_SeesEvidenceAndWorstCaseBeforeAnyRecommendation()
    {
        var shaped = new PresentationPolicy().Shape(Raw(HumanRole.Approver), HumanRole.Approver);
        Assert.False(shaped.RecommendationRevealed);

        int firstEvidence = shaped.Context.ToList().FindIndex(l => l.Contains("證據") || l.Contains("最壞"));
        int recommendation = shaped.Context.ToList().FindIndex(l => l.Contains("建議"));
        Assert.True(firstEvidence >= 0);
        Assert.True(recommendation < 0 || firstEvidence < recommendation,
            "系統建議排在證據前面 → 直接誘發確認偏誤");

        // 揭示是一個獨立動作，必須在人表態之後才呼叫
        var revealed = RecommendationReveal.Reveal(shaped, "部署 A");
        Assert.True(revealed.RecommendationRevealed);
        Assert.Contains(revealed.Context, l => l.Contains("部署 A"));
    }

    [Fact]
    public void Rater_NeverSeesModelIdentityOrSelfReportedConfidence()
    {
        var raw = Raw(HumanRole.Rater) with
        {
            Options = ImmutableArray.Create("[gpt-4o｜自評信心 95%] 這是 timer 競態", "[claude｜自評信心 60%] 這是資源提前釋放")
        };
        var shaped = new PresentationPolicy().Shape(raw, HumanRole.Rater);

        Assert.All(shaped.Options, o =>
        {
            Assert.DoesNotContain("gpt-4o", o);
            Assert.DoesNotContain("claude", o);
            Assert.DoesNotContain("自評信心", o);
        });
        Assert.DoesNotContain(shaped.Context, l => l.Contains("建議"));
    }

    [Fact]
    public void UtilityOwner_DoesNotSeeWhichActionTheSystemPrefers()
    {
        var shaped = new PresentationPolicy().Shape(Raw(HumanRole.UtilityOwner), HumanRole.UtilityOwner);
        Assert.Null(shaped.SystemRecommendation);
        Assert.DoesNotContain(shaped.Context, l => l.Contains("建議"));
    }

    [Fact]
    public void MutationSwitch_RecommendationFirst_ShowsTheAnswerBeforeAsking()
    {
        var shaped = new RecommendationFirstPolicy().Shape(Raw(HumanRole.Approver), HumanRole.Approver);
        Assert.True(shaped.RecommendationRevealed);
        Assert.Contains("建議", shaped.Context[0]);
    }
}

// ── 潛台詞：主動抽樣與盲評 ──────────────────────────────────────────────
public class SubtextTests
{
    private static SubtextSample S(string id, params string[] readings)
        => new(id, "他這句話的意思是？",
               readings.Select((t, i) => new Reading($"agent-{i}", t, 0.5)).ToImmutableArray());

    [Fact]
    public void CalibrationSet_TakesTheMostContestedSamples_AuditSetStaysRandom()
    {
        var pool = new[]
        {
            S("S1", "他在婉拒", "他在婉拒"),                                    // 完全一致 → 分歧 0
            S("S2", "他在婉拒", "他其實是在要更多預算"),                        // 分歧大
            S("S3", "他在確認時程", "他在質疑可行性並暗示要換人做"),            // 分歧更大
        };

        var sampler = new TopKDisagreementSampler(new NGramDisagreement());
        var picked = sampler.Select(pool, calibrationK: 1, auditK: 1, new DecisionAI.Adapters.Runtime.SeededRandomSource(3));

        var calib = picked.Where(p => p.Purpose == SamplePurpose.Calibration).ToList();
        Assert.Single(calib);
        Assert.NotEqual("S1", calib[0].Sample.Id);                              // 一致的樣本不值得花人評額度

        var audit = picked.Where(p => p.Purpose == SamplePurpose.Audit).ToList();
        Assert.Single(audit);
        Assert.Contains("隨機", audit[0].Why);
    }

    [Fact]
    public void AuditSet_IsNotTheTopKByDisagreement_AcrossSeeds()
    {
        var pool = Enumerable.Range(0, 8)
            .Select(i => S($"S{i}", "讀法甲" + new string('甲', i), "讀法乙" + new string('乙', 8 - i))).ToList();
        var sampler = new TopKDisagreementSampler(new NGramDisagreement());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 20; seed++)
            foreach (var p in sampler.Select(pool, 2, 1, new DecisionAI.Adapters.Runtime.SeededRandomSource(seed))
                                     .Where(p => p.Purpose == SamplePurpose.Audit))
                seen.Add(p.Sample.Id);

        Assert.True(seen.Count > 1, "稽核集每次都抽到同一個樣本 → 那不是隨機抽樣");

        // 壞的守門件：稽核集也用主動學習挑 → 永遠是分歧最大的那幾個
        var bad = new ActiveAuditSampler(new NGramDisagreement());
        var frozen = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 20; seed++)
            foreach (var p in bad.Select(pool, 2, 1, new DecisionAI.Adapters.Runtime.SeededRandomSource(seed))
                                 .Where(p => p.Purpose == SamplePurpose.Audit))
                frozen.Add(p.Sample.Id);
        Assert.Single(frozen);
    }

    [Fact]
    public void BlindBallot_RandomizesOrderAcrossSeeds_ButStaysReplayableForOneSeed()
    {
        var sample = S("S1", "讀法甲", "讀法乙", "讀法丙");
        var orders = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 20; seed++)
            orders.Add(string.Join(",", BlindRaterGateway.Prepare(sample,
                new DecisionAI.Adapters.Runtime.SeededRandomSource(seed)).AgentIdByPosition));
        Assert.True(orders.Count > 1, "候選順序永遠一樣 → 第一個位置天生占優（position bias）");

        var a = BlindRaterGateway.Prepare(sample, new DecisionAI.Adapters.Runtime.SeededRandomSource(9));
        var b = BlindRaterGateway.Prepare(sample, new DecisionAI.Adapters.Runtime.SeededRandomSource(9));
        Assert.Equal(a.AgentIdByPosition.ToArray(), b.AgentIdByPosition.ToArray());                 // 同種子可重放
    }

    [Fact]
    public async Task BlindRating_MapsThePositionBackToTheRightAgent()
    {
        var sample = S("S1", "讀法甲", "讀法乙", "讀法丙");
        var humans = new ScriptedHumanGateway(_ => new HumanVerdict(true, "選了第二個") { ChosenOption = 1 });
        var rng = new DecisionAI.Adapters.Runtime.SeededRandomSource(11);

        var expected = BlindRaterGateway.Prepare(sample, new DecisionAI.Adapters.Runtime.SeededRandomSource(11)).AgentIdByPosition[1];
        var result = await new BlindRaterGateway(humans, new PresentationPolicy()).RateAsync(sample, rng);
        Assert.Equal(expected, result.WinnerAgentId);
        Assert.False(result.NoVerdict);
    }
}

// ── 多樣性處置階梯 ──────────────────────────────────────────────────────
public class DiversityRemedyTests
{
    private static readonly DiversityVerdict Collapsed =
        new(true, ImmutableDictionary<string, double>.Empty, ImmutableArray.Create("distinct-3gram"), "崩塌");

    [Fact]
    public void SecondStep_MustSwitchFamily_NotJustResampleTheSameModel()
    {
        var ladder = new DiversityRemedy();
        Assert.Equal(RemedyKind.RaiseTemperature, ladder.Next(Collapsed, 0).Kind);
        Assert.Equal(RemedyKind.SwitchFamily, ladder.Next(Collapsed, 1).Kind);
        Assert.Equal(RemedyKind.InjectDiversityPrompt, ladder.Next(Collapsed, 2).Kind);
        Assert.Equal(RemedyKind.FacilityLocationSubset, ladder.Next(Collapsed, 3).Kind);
        Assert.Equal(RemedyKind.GiveUp, ladder.Next(Collapsed, 4).Kind);
    }

    [Fact]
    public void NotCollapsed_MeansNoRemedy()
        => Assert.Equal(RemedyKind.None, new DiversityRemedy().Next(
            new DiversityVerdict(false, ImmutableDictionary<string, double>.Empty, ImmutableArray<string>.Empty, ""), 0).Kind);

    [Fact]
    public void MinimalDegradation_TightensTheThresholds()
    {
        var tight = DiversityThresholds.Default.Tightened();
        foreach (var kv in DiversityThresholds.Default.MinByMetric)
            Assert.True(tight.MinByMetric[kv.Key] > kv.Value,
                "同 family 的表面差異不代表真多樣性 → 降級時門檻必須提高");
    }

    [Fact]
    public void FacilityLocation_PicksTheLeastSimilarSubset()
    {
        var candidates = new[]
        {
            new Candidate("c1", "改鎖住共享狀態", "A", "x"),
            new Candidate("c2", "改鎖住共享狀態（同義）", "B", "x"),
            new Candidate("c3", "整個拿掉 timer 改用連線層心跳", "C", "y"),
        };
        var pick = DiversityRemedy.PickDiverseSubset(candidates, 2).Select(c => c.Id).ToList();
        Assert.Contains("c3", pick);        // 最不像的那個一定要被選進來
    }

    [Fact]
    public void MutationSwitch_AlwaysPassMonitor_NeverReportsCollapse()
    {
        var identical = Enumerable.Range(0, 4)
            .Select(i => new Candidate($"c{i}", "一模一樣的答案", $"A{i}", "x")).ToList();
        Assert.True(new DiversityMonitor().Evaluate(identical, DiversityThresholds.Default).Collapsed);
        Assert.False(new AlwaysPassMonitor().Evaluate(identical, DiversityThresholds.Default).Collapsed);
    }

    [Fact]
    public void LocalEmbedder_IsDeterministicAndSeparatesDifferentTexts()
    {
        var e = new DecisionAI.Adapters.Runtime.LocalEmbedder();
        Assert.Equal(e.Embed("timer 競態"), e.Embed("timer 競態"));     // 可重放的前提

        var metric = new EmbeddingPairwise(e);
        double same = metric.Score(new[]
        {
            new Candidate("a", "timer 與關閉流程競爭同一個 channel", "A", "x"),
            new Candidate("b", "timer 與關閉流程競爭同一個 channel", "B", "y")
        });
        double diff = metric.Score(new[]
        {
            new Candidate("a", "timer 與關閉流程競爭同一個 channel", "A", "x"),
            new Candidate("b", "交換機的 idle timeout 把閒置連線砍掉了", "B", "y")
        });
        Assert.True(diff > same);
    }
}

// ── 實驗歷史頻率優先於定性估計 ──────────────────────────────────────────
public class ExperimentCatalogTests
{
    private const string Desc = "停用 KeepAlive timer，連跑 5000 次循環";

    private static ExperimentDraft Draft() => new(
        "NOM-1", Desc, ImmutableArray.Create("下降", "無變化"),
        ImmutableArray.Create(new LikertVote("H1", ImmutableArray.Create(LikertBelief.Likely, LikertBelief.Unlikely))),
        "designer-E", 0.4);

    private static ElicitationContext Ctx(IExperimentCatalog catalog)
        => new(ImmutableDictionary<string, string>.Empty.Add("H1", "CLM-race"), catalog.View(catalog.CurrentVersion));

    [Fact]
    public void WithEnoughHistory_FrequenciesReplaceTheQualitativeEstimate()
    {
        var catalog = new InMemoryExperimentCatalog().Seed(Desc, "CLM-race", 9, 1);
        var (exp, skip) = new LikertLikelihoodElicitor().Register(Draft(), "EXP-1", Ctx(catalog));

        Assert.Null(skip);
        Assert.Equal("catalog_frequency", exp!.LikelihoodSource);
        // 歷史是數出來的，不是估出來的：9:1 的頻率不該等於「很可能」的 0.75
        Assert.NotEqual(Likert.ToProbability(LikertBelief.Likely), exp.LikelihoodByClaim["H1"][0], 3);
    }

    [Fact]
    public void WithoutEnoughHistory_TheQualitativeLadderIsUsed()
    {
        var catalog = new InMemoryExperimentCatalog().Seed(Desc, "CLM-race", 2, 1);   // < MinHistory
        var (exp, _) = new LikertLikelihoodElicitor().Register(Draft(), "EXP-1", Ctx(catalog));
        Assert.Equal("likert_median", exp!.LikelihoodSource);
        Assert.Equal(Likert.ToProbability(LikertBelief.Likely), exp.LikelihoodByClaim["H1"][0], 3);
    }

    [Fact]
    public void ExperimentWithFewerThanTwoOutcomes_IsNotRegistered()
    {
        var draft = Draft() with { Outcomes = ImmutableArray.Create("只有一種結果") };
        var (exp, skip) = new LikertLikelihoodElicitor().Register(draft, "EXP-1", Ctx(new InMemoryExperimentCatalog()));
        Assert.Null(exp);
        Assert.Contains("無法區分", skip);
    }

    [Fact]
    public void VotesForClaimsThatDoNotExist_AreNotEnoughToRegister()
    {
        var draft = Draft() with
        {
            Votes = ImmutableArray.Create(new LikertVote("H9", ImmutableArray.Create(LikertBelief.Likely, LikertBelief.Unlikely)))
        };
        var (exp, skip) = new LikertLikelihoodElicitor().Register(draft, "EXP-1", Ctx(new InMemoryExperimentCatalog()));
        Assert.Null(exp);
        Assert.Contains("防事後解釋", skip);
    }
}

// ── 角色資格探針 ────────────────────────────────────────────────────────
public class ProbeTests
{
    [Fact]
    public async Task CriticProbe_ScoresByHowManyPlantedErrorsAreCaught()
    {
        string claims = "H1: 看起來沒問題\nH2: 植入的錯\nH3: 另一個植入的錯";
        var probe = new CriticProbe(claims, "H2", "H3");

        var sharp = Agent("sharp", """
            {"critiques":[{"claimId":"H2","issue":"錯","severity":0.9},{"claimId":"H3","issue":"錯","severity":0.8}]}
            """);
        var blunt = Agent("blunt", """
            {"critiques":[{"claimId":"H1","issue":"沒依據的質疑","severity":0.9}]}
            """);

        var good = await probe.RunAsync(sharp);
        var bad = await probe.RunAsync(blunt);
        Assert.True(good.Passed);
        Assert.False(bad.Passed);
        Assert.True(good.Score > bad.Score);
    }

    [Fact]
    public async Task UnfitAgent_IsExcludedFromTheRoleAfterEnoughProbeRuns()
    {
        var registry = new AgentRegistry();
        foreach (var (spec, llm) in AtsScenario.Agents()) registry.Register(spec, llm);

        var failed = PolicySnapshot.Initial.Apply(new PolicyDelta("CASE-PROBE",
            ImmutableArray<WeightObservation>.Empty, ImmutableArray<CatalogEntryCandidate>.Empty,
            ImmutableArray<string>.Empty)
        {
            ProbeResults = ImmutableArray.Create(
                new ProbeResult("solver-A", "solver", false, 0.1),
                new ProbeResult("solver-A", "solver", false, 0.1),
                new ProbeResult("solver-A", "solver", false, 0.1))
        });
        Assert.True(failed.Fit("solver-A", "solver").ProbeRuns >= 2);

        var demand = new RoleDemand(ImmutableArray.Create(("solver", 2)), AtsScenario.Domain);
        for (int seed = 0; seed < 10; seed++)
        {
            var r = new RoleAssigner(registry).Assign(demand, failed, new DecisionAI.Adapters.Runtime.SeededRandomSource(seed));
            Assert.DoesNotContain("solver-A", r.AgentsFor("solver"));
        }
    }

    private static RegisteredAgent Agent(string id, string output)
    {
        var registry = new AgentRegistry();
        registry.Register(
            new AgentSpec(id, "vendor-t", "t-family", "gen-1", ImmutableArray.Create("critic"),
                          ImmutableArray.Create("*"), new CostProfile(1, 1)),
            new ScriptedLlm(id, _ => output));
        return registry.Get(id);
    }
}
