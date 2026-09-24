// ============================================================================
//  Phase 3 完成門檻：序列型測試（30–50 案）
//
//  單案測不出學習。這裡連跑 40 個 case，共用同一份 PolicySnapshot、Catalog、
//  實驗歷史與 conformal 樣本庫，然後問四件事：
//    1) Beta 後驗有沒有收斂到 agent 的真實能力（我們事先就知道那個數字）
//    2) 校準有沒有把過度自信的 agent 修正回來
//    3) Catalog 命中率有沒有隨案例成長
//    4) conformal 的實測覆蓋率有沒有接近宣稱的 1−α
//
//  每一條都配一個對照：拿掉學習之後那條曲線必須平掉。
//  沒有對照的話，「數字往好的方向走」可能只是因為它本來就長那樣。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Probability;
using DecisionAI.Testing;
using Xunit;
using Xunit.Abstractions;

namespace DecisionAI.Tests;

public class SequentialLearningTests
{
    private readonly ITestOutputHelper _out;
    public SequentialLearningTests(ITestOutputHelper output) => _out = output;

    private const int N = 40;

    private sealed record Run(
        TestSystem Sys,
        ImmutableArray<SeqCase> Cases,
        PolicySnapshot Final,
        ImmutableArray<bool> CatalogHitPerCase,
        ImmutableArray<ImmutableArray<string>> SolversPerCase,
        IConformalSampleStore Conformal);

    private static Run RunSequence(int n = N, bool learn = true)
    {
        var cases = SequentialScenario.Cases(n);
        var byId = cases.ToDictionary(c => c.Id);
        var catalog = new InMemoryClaimCatalog();          // 刻意從空的開始：命中率要能被看到成長

        var sys = TestSystem.Build(new TestSystemOptions
        {
            IncludeL1Critic = false,
            IncludeL4Experiment = false,
            ClaimCatalog = catalog,
            L3Test = SequentialScenario.Verifier(byId),
            Agents = () => SequentialScenario.Agents.Select(a =>
                (Spec(a), (DecisionAI.Core.Ports.ILlm)SequentialScenario.Persona(a, byId)))
        });

        var hits = ImmutableArray.CreateBuilder<bool>();
        var solvers = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var policy = sys.PolicyStore.Current;

        foreach (var c in cases)
        {
            var run = sys.Orchestrator.RunAsync(c.Id, SequentialScenario.Request(c), SequentialScenario.Evidence(c))
                         .GetAwaiter().GetResult();
            var s = run.State;

            // 本案的真機制有沒有對到 Catalog 的既有條目（而不是又開一個新條目）
            var trueClaim = s.Claims.FirstOrDefault(k => k.Frame.Mechanism == c.TrueMechanism);
            hits.Add(trueClaim is not null && trueClaim.Key.IsCatalogued);
            solvers.Add(s.Roles.Where(x => x.Role == "solver").Select(x => x.AgentId).ToImmutableArray());

            if (learn) policy = sys.Orchestrator.RecordOutcome(run.Journal, SequentialScenario.OutcomeFor(c, s));
            else run.Journal.ApplyOrThrow(new OutcomeRecorded(SequentialScenario.OutcomeFor(c, s))
                                          .By("outcome", "evaluation", Actors.Evaluation));
        }

        return new Run(sys, cases, policy, hits.ToImmutable(), solvers.ToImmutable(), sys.ConformalStore);
    }

    private static AgentSpec Spec(SeqAgent a) => new(
        a.Id, a.Vendor, a.Family, "gen-1",
        ImmutableArray.Create("solver", "critic", "experiment_designer", "briefer"),
        ImmutableArray.Create(SequentialScenario.Domain, "*"),
        new CostProfile(0.5, 1.5));

    // ── 門檻 1：Beta 後驗收斂到真實能力 ────────────────────────────────
    [Fact]
    public void BetaPosterior_ConvergesToTheDeclaredTrueAccuracy()
    {
        var r = RunSequence();
        foreach (var a in SequentialScenario.Agents)
        {
            var w = r.Final.Weight(a.Id, SequentialScenario.Domain, "solver");
            double actual = r.Cases.Count(c => SequentialScenario.IsCorrectOn(a, c.Id)) / (double)r.Cases.Length;
            _out.WriteLine($"{a.Id}：宣告能力 {a.Accuracy:P0}、本序列實際 {actual:P0}、學到的後驗 {w.Mean:P0}（n={w.N}）");
        }

        // 被持續選用的 agent 必須收斂。被淘汰的那個樣本本來就少——
        // 那不是收斂失敗，是 Thompson 開始 exploit 了（下一條斷言就是在測這件事）。
        foreach (var a in SequentialScenario.Agents)
        {
            var w = r.Final.Weight(a.Id, SequentialScenario.Domain, "solver");
            if (w.N < 10) continue;
            double actual = HitRateOn(a, r, only: r.SolversPerCase);
            Assert.True(Math.Abs(w.Mean - actual) < 0.12,
                $"{a.Id} 的後驗 {w.Mean:P0} 與他被選用的那些案子上的實際命中率 {actual:P0} 差太多 → 學習沒有在學");
        }

        var hi = r.Final.Weight("solver-hi", SequentialScenario.Domain, "solver");
        Assert.True(hi.N >= 25, $"最強的 agent 只被選用 {hi.N} 次 → 序列太短或探索過度");

        // 排序必須正確：能力最高的那個，後驗也要最高
        var ranked = SequentialScenario.Agents
            .Select(a => (a.Id, mean: r.Final.Weight(a.Id, SequentialScenario.Domain, "solver").Mean))
            .OrderByDescending(x => x.mean).ToList();
        Assert.Equal("solver-hi", ranked[0].Id);
        Assert.Equal("solver-low", ranked[^1].Id);
    }

    // ── 門檻 1b：Thompson 用在已經量清楚的 arm 上 exploit，對不確定的 arm 繼續探索 ──
    [Fact]
    public void ThompsonSampling_ExploitsWhatIsMeasured_AndKeepsProbingTheUncertainArm()
    {
        var r = RunSequence();
        int half = r.SolversPerCase.Length / 2;
        int earlySlots = r.SolversPerCase.Take(half).Sum(x => x.Length);
        int lateSlots = r.SolversPerCase.Skip(half).Sum(x => x.Length);
        double Early(string id) => r.SolversPerCase.Take(half).Sum(x => x.Count(a => a == id)) / (double)earlySlots;
        double Late(string id) => r.SolversPerCase.Skip(half).Sum(x => x.Count(a => a == id)) / (double)lateSlots;

        foreach (var a in SequentialScenario.Agents)
        {
            var w = r.Final.Weight(a.Id, SequentialScenario.Domain, "solver");
            _out.WriteLine($"{a.Id}：前半佔 {Early(a.Id):P0} 的 solver 名額、後半 {Late(a.Id):P0}；" +
                           $"後驗 {w.Mean:P0}±{Math.Sqrt(w.Variance):F2}（n={w.N}，真實能力 {a.Accuracy:P0}）");
        }

        // 後半的名額分配必須與能力同向：最強的拿最多、最弱的拿最少
        var lateShare = SequentialScenario.Agents.Select(a => (a.Id, share: Late(a.Id)))
                                                 .OrderByDescending(x => x.share).ToList();
        Assert.Equal("solver-hi", lateShare[0].Id);
        Assert.Equal("solver-low", lateShare[^1].Id);

        // 最弱的那個仍然會被抽到——這不是 bug，是 Thompson 的本意：
        // 他的後驗最寬（樣本最少），抽樣時自然還有機會勝出。停止探索才是問題：
        // 後驗會停在「當初剛好抽到的那幾次」上，再也不會被修正，
        // 而他的盲點會從個別模型的問題升格成系統每一案都漏同一類錯。
        Assert.True(Late("solver-low") > 0, "後半完全沒有再抽到最弱的 agent → 沒有探索，後驗從此不會再被修正");
        Assert.True(Late("solver-hi") < 1.0, "最強的 agent 佔滿了所有名額 → 沒有留任何探索空間");

        // 而且探索是有效的：最弱的那個也累積了足夠樣本，讓他的後驗確實收窄
        var low = r.Final.Weight("solver-low", SequentialScenario.Domain, "solver");
        Assert.True(low.N >= 3, $"最弱的 agent 只累積了 {low.N} 筆 → 探索太少，他的能力其實沒有被量過");
        Assert.True(Math.Sqrt(low.Variance) < Math.Sqrt(BetaPosterior.Prior.Variance),
            "最弱的 agent 的後驗沒有比先驗更窄 → 抽到他卻沒有從結果裡學到東西");
    }

    /// <summary>只算「他真的被選用」的那些案子的命中率——沒被選用的案子他沒有表現可言。</summary>
    private static double HitRateOn(SeqAgent a, Run r, ImmutableArray<ImmutableArray<string>> only)
    {
        var used = r.Cases.Where((c, i) => only[i].Contains(a.Id)).ToList();
        return used.Count == 0 ? 0 : used.Count(c => SequentialScenario.IsCorrectOn(a, c.Id)) / (double)used.Count;
    }

    // ── 門檻 2：校準把自報信心拉回實際命中率（兩個方向都要會）──────────
    [Fact]
    public void Calibration_PullsStatedConfidenceTowardTheRealHitRate_InBothDirections()
    {
        var r = RunSequence();
        var engine = new CalibrationEngine();

        var loud = SequentialScenario.Agents.First(a => a.Id == "solver-loud");   // 喊 90%，只有 60% 對
        var hi = SequentialScenario.Agents.First(a => a.Id == "solver-hi");       // 喊 75%，其實 85% 對

        var curve = r.Final.Curve(loud.Id);
        Assert.True(curve.Records.Length >= 30, $"校準樣本只有 {curve.Records.Length} 筆 → 回校沒有意義");
        _out.WriteLine($"{loud.Id}：Brier {curve.Brier:F3}、ECE {curve.Ece():F3}、收縮係數 {curve.ShrinkFactor:F2}");

        double loudStated = Likert.ToProbability(loud.Confidence);
        double loudRecal = engine.Recalibrate(loud.Id, loudStated, r.Final);
        double hiStated = Likert.ToProbability(hi.Confidence);
        double hiRecal = engine.Recalibrate(hi.Id, hiStated, r.Final);
        _out.WriteLine($"{loud.Id}：自評 {loudStated:P0} → 校準後 {loudRecal:P0}（實際能力 {loud.Accuracy:P0}）");
        _out.WriteLine($"{hi.Id}：自評 {hiStated:P0} → 校準後 {hiRecal:P0}（實際能力 {hi.Accuracy:P0}）");

        // 過度自信要被拉下來，而且要拉到接近他真正的命中率，不是只降一點交差
        Assert.True(loudRecal < loudStated, "過度自信沒有被修正");
        Assert.True(Math.Abs(loudRecal - loud.Accuracy) < 0.12,
            $"校準後 {loudRecal:P0} 離真實能力 {loud.Accuracy:P0} 還很遠 → 修得太保守，等於沒修");

        // 反方向同樣要會：低估自己的 agent 應該被拉上來。
        // 只會往 0.5 收的校準看起來很安全，實際上會把可靠的 agent 一起壓低。
        Assert.True(hiRecal > hiStated, "低估自己的 agent 沒有被拉上來 → 校準只會往保守方向壓");
        Assert.True(Math.Abs(hiRecal - hi.Accuracy) < 0.12,
            $"校準後 {hiRecal:P0} 離真實能力 {hi.Accuracy:P0} 太遠");

        // 對照：不回校的話，90% 就是 90%，過度自信的 agent 直接主導後驗
        Assert.Equal(loudStated, new NoOpCalibrator().Recalibrate(loud.Id, loudStated, r.Final), 3);
    }

    // ── 門檻 3：Catalog 命中率隨案例成長 ───────────────────────────────
    [Fact]
    public void CatalogHitRate_GrowsAsTheDomainAccumulatesMemory()
    {
        var r = RunSequence();
        double early = r.CatalogHitPerCase.Take(10).Count(x => x) / 10.0;
        double late = r.CatalogHitPerCase.Skip(r.CatalogHitPerCase.Length - 10).Count(x => x) / 10.0;
        _out.WriteLine($"Catalog 命中率：前 10 案 {early:P0} → 後 10 案 {late:P0}（catalog 最終 {r.Sys.ClaimCatalog.CurrentVersion} 條）");

        Assert.True(late > early, $"命中率沒有成長（{early:P0} → {late:P0}）→ 跨 case 記憶沒有在累積");
        Assert.True(late >= 0.9, $"跑完 {N} 案後命中率仍只有 {late:P0} —— 五種機制早就該全部進 Catalog 了");

        // append-only：條目數不可能超過機制數（同一個 Mechanism|Locus 只能有一條）
        Assert.True(r.Sys.ClaimCatalog.CurrentVersion <= SequentialScenario.Pool.Length,
            "Catalog 長出了比機制數更多的條目 → 同一個故障被重複登記");

        // 對照：不回填 outcome 就不會有新條目（Catalog 只能由 Evaluation 推進）
        var noLearn = RunSequence(learn: false);
        Assert.Equal(0, noLearn.Sys.ClaimCatalog.CurrentVersion);
        Assert.DoesNotContain(true, noLearn.CatalogHitPerCase);
    }

    // ── 門檻 4：conformal 的實測覆蓋率接近宣稱值 ───────────────────────
    [Fact]
    public void ConformalCoverage_IsMeasuredOnTheAuditSet_AndLandsNearTheTarget()
    {
        var r = RunSequence();
        var quantiles = r.Conformal.RebuildAll();
        Assert.True(quantiles.ContainsKey(TaskFamilies.Diagnosis), "白名單題型沒有累積到任何分位數");

        var q = quantiles[TaskFamilies.Diagnosis];
        double coverage = r.Conformal.EmpiricalCoverage(TaskFamilies.Diagnosis, q);
        _out.WriteLine($"conformal：校準 {q.CalibrationSize} 筆、稽核 {q.AuditSize} 筆、門檻 {q.Threshold:F3}、" +
                       $"實測覆蓋率 {coverage:P0}（目標 {1 - q.Alpha:P0}）");

        Assert.True(q.CalibrationSize >= 20, $"校準集只有 {q.CalibrationSize} 筆 → 分位數還不能用");
        Assert.True(q.AuditSize >= 5, $"稽核集只有 {q.AuditSize} 筆 → 覆蓋率量不準");

        // 有限樣本下不會剛好等於 90%，但偏離太多就代表可交換性假設在這裡不成立
        Assert.InRange(coverage, 0.70, 1.0);

        // 而且這時候覆蓋層才真的會被發出來——樣本不足時它必須是 null
        var calibrator = new ConformalCalibrator();
        var policy = r.Final with { Conformal = quantiles };
        var beliefs = new Dictionary<string, double> { ["H1"] = 0.7, ["H2"] = 0.2, ["H3"] = 0.1 };
        var layer = calibrator.Predict(beliefs, TaskFamilies.Diagnosis, policy);
        Assert.NotNull(layer);
        Assert.NotEmpty(layer!.PredictionSet);
    }

    // ── 門檻 5：錯誤相關性從真實結果裡學出來 ───────────────────────────
    [Fact]
    public void ErrorCorrelation_IsLearnedFromOutcomes_NotAssumedFromLineage()
    {
        var r = RunSequence();
        var probe = new ProbeCalibratedEstimator();
        var ctx = new CorrelationContext(
            SequentialScenario.Agents.ToImmutableDictionary(a => a.Id, a => new AgentLineage(a.Vendor, a.Family, "gen-1")),
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty,
            r.Final);

        var pairs = r.Final.Correlation.Pairs;
        Assert.NotEmpty(pairs);
        foreach (var kv in pairs.OrderBy(k => k.Key.A, StringComparer.Ordinal))
            _out.WriteLine($"{kv.Key.A} × {kv.Key.B}：phi {kv.Value.Phi:F2}（{kv.Value.Shared} 筆共同觀察）");

        // 兩個高能力的 agent 比較常同時答對 → 相關性高於「一高一低」的那一對
        var (hiLoud, _) = probe.Estimate("solver-hi", "solver-loud", ctx);
        var (hiLow, _) = probe.Estimate("solver-hi", "solver-low", ctx);
        _out.WriteLine($"探針層估計：hi×loud {hiLoud:F2}、hi×low {hiLow:F2}");
        Assert.True(hiLoud > hiLow,
            "兩個常常一起答對的 agent 沒有被判定為更相關 → 集成會把重複的證據當成獨立的");

        // 對照：沒有共同觀察時，探針層必須回報 0 筆樣本而不是編一個數字
        var empty = new CorrelationContext(ctx.Lineage, ctx.ClaimsByAgent, PolicySnapshot.Initial);
        Assert.Equal(0, probe.Estimate("solver-hi", "solver-low", empty).Item2);
    }
}
