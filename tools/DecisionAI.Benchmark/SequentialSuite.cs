// ============================================================================
//  序列型套件（Phase 3）——跨案學習的量測與它的 mutation。
//
//  Phase 1/2 的 mutation 都是單案的：換掉守門件，這一案就變紅。
//  Phase 3 的守門件不是這種東西：校準、漂移偵測、橡皮圖章監控都要跨數十案才會顯形，
//  在單案上換掉它們什麼事都不會發生。所以它們的 mutation 也必須是序列型的——
//  跑完整串之後，看四條學習曲線有沒有走壞。
//
//  「跑完 40 案沒有任何檢查變紅」在這裡不是好消息，而是這個 mutation 沒被測到。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Evaluation;
using DecisionAI.Modules.Probability;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

public sealed record SequentialMetric(string Name, double Value, double? Expected, bool Pass, string Detail)
{
    /// <summary>漂移變體裡，某些指標「本來就該壞」——那是要展示的現象，不是失效。</summary>
    public bool ExpectedToDegrade { get; init; }
}

public sealed record SequentialResult(string Variant, ImmutableArray<SequentialMetric> Metrics)
{
    public bool AllPass => Metrics.All(m => m.Pass);
    public int Failed => Metrics.Count(m => !m.Pass);
}

public static class SequentialSuite
{
    /// <summary>
    /// 50 案。門檻文件寫的是 30–50，取上限是因為這裡有四個 agent 而每案只有兩個 solver 名額：
    /// 40 案時每人平均只拿到 20 筆，校準的分桶就湊不出足夠樣本。
    /// 「案數」不是重點，「每個 agent 累積到幾筆」才是。
    /// </summary>
    public const int Cases = 50;

    /// <summary>correlated：只有「一個 agent + 它的複製品 + 一個獨立的弱 agent」，而且三個名額全滿。</summary>
    public static SequentialResult Run(string variant, Func<TestSystemOptions, TestSystemOptions>? mutate = null,
                                       bool drifting = false, bool correlatedPool = false)
    {
        var cases = SequentialScenario.Cases(Cases);
        var byId = cases.ToDictionary(c => c.Id);
        var catalog = new InMemoryClaimCatalog();
        var agents = correlatedPool ? SequentialScenario.CorrelatedPool
                   : drifting ? SequentialScenario.DriftingAgents
                   : SequentialScenario.Agents;
        var risk = correlatedPool ? RiskLevel.High : RiskLevel.Medium;
        var humans = ScriptedHumanGateway.RubberStamp();      // 永遠同意、1.5 秒按下去

        var options = new TestSystemOptions
        {
            IncludeL1Critic = false,
            IncludeL4Experiment = false,
            ClaimCatalog = catalog,
            Human = humans,
            L3Test = SequentialScenario.Verifier(byId),
            Agents = () => agents.Select(a => (Spec(a), (DecisionAI.Core.Ports.ILlm)SequentialScenario.Persona(a, byId)))
        };
        var sys = TestSystem.Build(mutate?.Invoke(options) ?? options);

        var hits = new List<bool>();
        var brier = new List<double>();
        var systemCalibration = new List<(double P, bool Y)>();
        var topCorrect = new List<bool>();
        var tightened = new List<bool>();
        var driftNoted = new List<bool>();
        var policy = sys.PolicyStore.Current;
        foreach (var c in cases)
        {
            var run = sys.Orchestrator.RunAsync(c.Id, SequentialScenario.Request(c, risk), SequentialScenario.Evidence(c))
                         .GetAwaiter().GetResult();
            var s = run.State;

            var trueClaim = s.Claims.FirstOrDefault(k => k.Frame.Mechanism == c.TrueMechanism);
            hits.Add(trueClaim is not null && trueClaim.Key.IsCatalogued);

            // 系統宣稱的機率有多誠實：把它給真值的機率記下來，最後看分桶命中率
            if (trueClaim is not null && s.Beliefs.TryGetValue(trueClaim.LocalId, out var pTrue))
                systemCalibration.Add((pTrue, true));
            if (trueClaim is not null)
                foreach (var kv in s.Beliefs.Where(kv => kv.Key != trueClaim.LocalId))
                    systemCalibration.Add((kv.Value, false));

            // 系統自己的後驗有多準（多類 Brier）：校準與相關性折扣壞掉時，
            // argmax 可能還是對的，但機率會先變得不誠實——所以要量機率，不只量排序。
            if (trueClaim is not null && s.Beliefs.Count > 0)
                brier.Add(s.Beliefs.Sum(kv => Math.Pow(kv.Value - (kv.Key == trueClaim.LocalId ? 1 : 0), 2)));

            // 系統最後相信的那條，是不是真的那條
            string? top = s.Beliefs.Count == 0 ? null
                : s.Beliefs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            topCorrect.Add(top is not null && trueClaim is not null && top == trueClaim.LocalId);

            tightened.Add(s.Log.Any(l => l.Contains("automation bias 監控")));
            driftNoted.Add(s.Log.Any(l => l.Contains("校準漂移")));
            policy = sys.Orchestrator.RecordOutcome(run.Journal, SequentialScenario.OutcomeFor(c, s));
        }

        return new SequentialResult(variant, ImmutableArray.Create(
            BetaConvergence(policy, cases, agents) with { ExpectedToDegrade = drifting },
            CalibrationCorrection(policy, agents),
            CatalogGrowth(hits, sys),
            ConformalCoverage(sys),
            DecisionAccuracy(topCorrect) with { ExpectedToDegrade = drifting },
            PosteriorBrier(brier) with { ExpectedToDegrade = drifting },
            PosteriorHonesty(systemCalibration) with { ExpectedToDegrade = drifting },
            CalibratorEffect(policy, agents, sys.Calibrator),
            EveryAgentMeasured(policy, agents),
            DriftDetected(driftNoted, drifting),
            RubberStampDetected(tightened)));
    }

    // 5) 系統最後相信的那條是不是真的那條。校準與相關性折扣壞掉時，最先掉的就是它。
    private static SequentialMetric DecisionAccuracy(List<bool> topCorrect)
    {
        double rate = topCorrect.Count == 0 ? 0 : topCorrect.Count(x => x) / (double)topCorrect.Count;
        double late = topCorrect.Skip(topCorrect.Count / 2).Count(x => x) / (double)(topCorrect.Count / 2);
        return new("後驗命中率", Math.Round(rate, 3), 0.80, rate >= 0.80,
            $"全序列 {rate:P0}、後半 {late:P0}（{topCorrect.Count} 案）");
    }

    // 5b) 後驗機率本身的準度。排序可能還是對的，但機率會先變得不誠實。
    private static SequentialMetric PosteriorBrier(List<double> brier)
    {
        double mean = brier.Count == 0 ? 1 : brier.Average();
        return new("後驗 Brier", Math.Round(mean, 4), 0.45, mean <= 0.45,
            $"多類 Brier 平均 {mean:F3}（{brier.Count} 案；越小越準，0 = 完全正確且完全確定）");
    }

    // 5c) 系統自己的 ECE：它宣稱 70% 的那些事，是不是真的大約七成會發生。
    //     這是「機率誠不誠實」的直接量法，也是校準模組存在的理由——
    //     不回校的話，過度自信的 agent 會把系統的宣稱一起帶歪。
    private static SequentialMetric PosteriorHonesty(List<(double P, bool Y)> rows)
    {
        if (rows.Count == 0) return new("後驗誠實度（ECE）", 1, 0.15, false, "沒有任何後驗可量");
        double ece = new CalibrationCurve(rows.ToImmutableArray()).Ece() ?? 1;
        return new("後驗誠實度（ECE）", Math.Round(ece, 4), 0.15, ece <= 0.15,
            $"系統後驗的 ECE {ece:F3}（{rows.Count} 個機率宣稱；門檻 0.15）");
    }

    // 5d) 校準器本身有沒有用：把它套回這個 agent 自己的歷史，ECE 必須明顯下降。
    //     這是評估校準器的標準做法。它是 in-sample 的（用同一批資料擬合又評估），
    //     所以只能用來判定「有沒有在做事」，不能用來宣稱泛化——但要抓「根本沒在校準」，它就夠了。
    private static SequentialMetric CalibratorEffect(PolicySnapshot policy, ImmutableArray<SeqAgent> agents,
                                                     ICalibrator engine)
    {
        var rows = new List<(string Agent, double Raw, double Fixed_, int N)>();
        foreach (var a in agents)
        {
            var curve = policy.Curve(a.Id);
            if (curve.Records.Length < 20) continue;
            double raw = curve.Ece() ?? 1;
            var adjusted = new CalibrationCurve(curve.Records
                .Select(r => (engine.Recalibrate(a.Id, r.P, policy), r.Y)).ToImmutableArray());
            rows.Add((a.Id, raw, adjusted.Ece() ?? 1, curve.Records.Length));
        }
        if (rows.Count == 0) return new("校準器有效性", 1, 0.6, false, "沒有任何 agent 累積到 20 筆校準點");

        double worstRatio = rows.Max(r => r.Fixed_ / Math.Max(1e-6, r.Raw));
        return new("校準器有效性", Math.Round(worstRatio, 3), 0.6, worstRatio <= 0.6,
            string.Join("；", rows.Select(r => $"{r.Agent} ECE {r.Raw:F3} → {r.Fixed_:F3}（{r.N} 筆）")));
    }

    // 6) 每個 agent 都必須被量過。沒被量過的能力不是「未知」，是會被當成先驗 0.5 用下去。
    private static SequentialMetric EveryAgentMeasured(PolicySnapshot policy, ImmutableArray<SeqAgent> agents)
    {
        var counts = agents.ToDictionary(a => a.Id, a => policy.Weight(a.Id, SequentialScenario.Domain, "solver").N);
        int min = counts.Values.Min();
        return new("每個 agent 都被量過", min, 3, min >= 3,
            string.Join("；", counts.OrderBy(k => k.Value).Select(k => $"{k.Key} n={k.Value}")));
    }

    // 7) 漂移必須被系統自己抓到（看事件流，不是自己再算一次）
    //    自己再算一次的話，換掉系統裡的告警也不會有人發現——那種檢查抓不到任何東西。
    private static SequentialMetric DriftDetected(List<bool> driftNoted, bool drifting)
    {
        int n = driftNoted.Count(x => x);
        if (!drifting)
            return new("漂移偵測", n, 0, n == 0,
                n == 0 ? "本序列沒有漂移，系統也沒有誤報" : $"沒有人漂移卻有 {n} 案告警 → 誤報");
        return new("漂移偵測", n, 1, n > 0,
            n > 0 ? $"{n} 案在策略階段就標示了校準漂移並提高人類核准要求"
                  : "最強的 agent 在第 20 案後掉到 25%，但系統一案都沒有察覺");
    }

    // 8) 橡皮圖章化必須被抓到並收緊流程
    private static SequentialMetric RubberStampDetected(List<bool> tightened)
    {
        int n = tightened.Count(x => x);
        return new("橡皮圖章偵測", n, 1, n > 0,
            n > 0 ? $"{n} 案觸發了 automation bias 監控並收緊呈現（接受率 100%、1.5 秒按下去）"
                  : "連續 40 案全部秒速同意，卻沒有任何一案觸發監控");
    }

    // 1) Beta 後驗收斂到真實能力
    private static SequentialMetric BetaConvergence(PolicySnapshot policy, ImmutableArray<SeqCase> cases,
                                                   ImmutableArray<SeqAgent> agents)
    {
        var rows = agents
            .Select(a => (a, w: policy.Weight(a.Id, SequentialScenario.Domain, "solver")))
            .Where(x => x.w.N >= 10).ToList();
        if (rows.Count == 0)
            return new("Beta 收斂", 0, null, false, "沒有任何 agent 累積到 10 筆觀察");

        double worst = rows.Max(x => Math.Abs(x.w.Mean - x.a.Accuracy));
        // solver-hi 與 solver-twin 是同一個模型的兩個部署，誰在前面沒有意義；
        // 有意義的是「最強的那一對排在最前面」。
        bool ordered = rows.OrderByDescending(x => x.w.Mean).First().a.Id is "solver-hi" or "solver-twin";
        return new("Beta 收斂", Math.Round(worst, 3), 0.15, worst < 0.15 && ordered,
            string.Join("；", rows.Select(x => $"{x.a.Id} 真實 {x.a.Accuracy:P0} → 學到 {x.w.Mean:P0}（n={x.w.N}）")) +
            (ordered ? "" : "；排序錯誤"));
    }

    // 2) 校準把自報信心拉回實際命中率。
    //    修正的強度是「刻意」隨樣本數縮放的，所以樣本不足時要求完全修正等於要求它過度相信薄資料——
    //    那正是校準要避免的毛病。因此門檻分兩段：桶內樣本夠才要求落點，不夠只要求方向與比例。
    private static SequentialMetric CalibrationCorrection(PolicySnapshot policy, ImmutableArray<SeqAgent> agents)
    {
        var loud = agents.FirstOrDefault(a => a.Id == "solver-loud");
        if (loud is null) return new("校準修正", 0, null, true, "這個池子裡沒有過度自信的 agent → 不適用");

        double stated = Likert.ToProbability(loud.Confidence);
        double recal = new CalibrationEngine().Recalibrate(loud.Id, stated, policy);
        var curve = policy.Curve(loud.Id);
        var bucket = curve.Bucket(stated);
        int n = bucket?.N ?? 0;
        double gap = Math.Abs(recal - loud.Accuracy);

        bool ok = n >= 20
            ? gap < 0.12
            : recal < stated && (stated - recal) >= 0.5 * (n / 20.0) * (stated - loud.Accuracy);

        return new("校準修正", Math.Round(gap, 3), 0.12, ok,
            $"{loud.Id} 自評 {stated:P0} → 校準後 {recal:P0}（真實能力 {loud.Accuracy:P0}）；" +
            $"曲線 {curve.Records.Length} 筆、該桶 {n} 筆" +
            (n >= 20 ? "（樣本足，要求落點）" : $"（樣本不足，只要求修正量達應有的一半：修正 {stated - recal:F2}）"));
    }

    // 3) Catalog 命中率成長
    private static SequentialMetric CatalogGrowth(List<bool> hits, TestSystem sys)
    {
        double early = hits.Take(10).Count(x => x) / 10.0;
        double late = hits.Skip(hits.Count - 10).Count(x => x) / 10.0;
        bool ok = late > early && late >= 0.9 && sys.ClaimCatalog.CurrentVersion <= SequentialScenario.Pool.Length;
        return new("Catalog 命中率", Math.Round(late, 3), 0.9, ok,
            $"前 10 案 {early:P0} → 後 10 案 {late:P0}；catalog 最終 {sys.ClaimCatalog.CurrentVersion} 條" +
            $"（機制共 {SequentialScenario.Pool.Length} 種）");
    }

    // 4) conformal 實測覆蓋率（只用稽核集量）
    private static SequentialMetric ConformalCoverage(TestSystem sys)
    {
        var q = sys.ConformalStore.RebuildAll();
        if (!q.TryGetValue(TaskFamilies.Diagnosis, out var quantile))
            return new("conformal 覆蓋率", 0, 0.9, false, "白名單題型沒有累積到任何分位數");
        double coverage = sys.ConformalStore.EmpiricalCoverage(TaskFamilies.Diagnosis, quantile);
        bool ok = quantile.CalibrationSize >= 20 && quantile.AuditSize >= 5 && coverage >= 0.70;
        return new("conformal 覆蓋率", Math.Round(coverage, 3), 1 - quantile.Alpha, ok,
            $"校準 {quantile.CalibrationSize} 筆、稽核 {quantile.AuditSize} 筆、門檻 {quantile.Threshold:F3}、" +
            $"實測 {coverage:P0}（目標 {1 - quantile.Alpha:P0}）");
    }

    /// <summary>序列型 mutation：跨案的守門件只能這樣測。</summary>
    public static ImmutableArray<(string Id, string Name, string Target, string Why, bool OnDrifting,
                                  Func<TestSystemOptions, TestSystemOptions> Apply)> Mutations =>
        ImmutableArray.Create<(string, string, string, string, bool, Func<TestSystemOptions, TestSystemOptions>)>(
            ("S1", "不回校自報信心", "ICalibrator", "過度自信的 agent 直接主導後驗，校準曲線再準也沒人用",
                false, o => o with { Calibrator = new NoOpCalibrator() }),
            // S2 在序列套件上測不到，原因本身值得記下來：相關性折扣只改變「權重的相對大小」，
            // 而這個情境裡每個 agent 都會把同一批候選機制全部提出來，兩兩之間的 ρ 幾乎一樣，
            // 於是折扣對所有人等比例——等比例的權重在加權平均裡整個約掉。
            // 換句話說，錯誤相關折扣只有在「池子裡有人相關、有人不相關」時才會改變結論。
            // 它的守門功能由單案 benchmark 的 Ensemble 檢查（M16）負責，那裡查的是
            // 折扣有沒有真的被算出來並寫進事件流。
            ("S2", "相關性一律視為 0", "ICorrelationEstimator", "同源的一致答案被當成多份獨立證據",
                false, o => o with { Correlation = new ZeroCorrelation() }),
            ("S3", "角色指派改回 argmax mean", "IRoleAssigner", "領先者鎖死，弱者的能力永遠沒被量過",
                false, o => o with { RoleAssigner = null, ArgmaxAssigner = true }),
            // S4 只能在漂移序列上測：沒有漂移的序列裡，沉默的告警與正常的告警行為完全相同
            ("S4", "漂移告警永遠沉默", "IDriftAlarm", "學習訊號歪掉時仍照常用歷史省 solver",
                true, o => o with { Drift = new SilentDriftAlarm() }),
            ("S5", "不監控人類決策", "IAutomationBiasMonitor", "接受率 100% 也不會收緊流程",
                false, o => o with { Oversight = new BlindOversightMonitor() }));

    private static AgentSpec Spec(SeqAgent a) => new(
        a.Id, a.Vendor, a.Family, "gen-1",
        ImmutableArray.Create("solver", "critic", "experiment_designer", "briefer"),
        ImmutableArray.Create(SequentialScenario.Domain, "*"),
        new CostProfile(0.5, 1.5));
}
