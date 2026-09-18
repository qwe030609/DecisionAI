// ============================================================================
//  九項檢查 —— 全部可被推翻（falsifiable）。
//  對照組（ChatGPT 版）的 Profile 檢查是 profile ∈ profile±0.1，恆真；
//  Output 檢查等於 Routing 的推論。這裡每一項都由「宣告的金標」與「真實事件流」比對。
//  ★ 為 Rev2 新增的檢查；☆ 為 Phase 2 新增。
//
//  Phase 2 的五項刻意都是「獨立重算」而不是「讀它自己寫的數字」：
//  檢查自己算一次 EVOI、自己看後驗首位是誰，再跟事件流裡的宣告比對。
//  讀它自己寫的數字永遠會對，那種檢查抓不到任何壞掉的守門件。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Decision;
using DecisionAI.Modules.Probability;
using DecisionAI.Orchestration;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

public sealed record CheckResult(string Name, bool Pass, string Detail);

public static class Checks
{
    public static ImmutableArray<CheckResult> RunAll(BenchCase c, CaseRun run, TestSystem sys)
        => ImmutableArray.Create(
            Triage(c, run, sys), Routing(c, run.State), Evidence(c, run.State, sys), Workflow(run.State),
            Verification(c, run), Assurance(c, run), Permission(run), Canonicalization(c, run), Independence(c, run),
            Stability(c, run), Saturation(c, run), Coverage(c, run), ExperimentSelection(c, run), Ensemble(c, run));

    // 1) Triage：判定與宣告的金標一致；拒答時不得產生計畫
    private static CheckResult Triage(BenchCase c, CaseRun run, TestSystem sys)
    {
        var a = run.Report.Abstention;
        bool abstained = a.Abstained;
        bool dispositionOk = (c.Expected.Disposition == Disposition.Abstain) == abstained;
        bool codeOk = c.Expected.Disposition != Disposition.Abstain || a.Code == c.Expected.Code;
        string detail = abstained
            ? $"拒答 {a.Code}（期望 {c.Expected.Code}）；替代交付 {a.Payload?.Kind ?? "無"}；LLM 呼叫 {sys.LlmCallCount} 次、成本 {run.State.CostSpent:F3}"
            : $"進入流程（期望 {c.Expected.Disposition}）";
        return new("Triage", dispositionOk && codeOk, detail);
    }

    // 2) Routing：策略、workflow 與 critic 取捨都必須與宣告一致
    private static CheckResult Routing(BenchCase c, CaseState s)
    {
        if (c.Expected.Disposition == Disposition.Abstain)
            return new("Routing", s.Plan is null,
                s.Plan is null ? "拒答 → 未產生計畫（未花任何 token 規劃）" : $"不該有計畫卻產生了 {s.Plan.WorkflowName}");

        bool ok = s.Plan is not null && s.Plan.Strategy == c.Expected.Strategy
                  && s.Plan.WorkflowName == c.Expected.Workflow && s.Plan.IncludeCritic == c.Expected.ExpectCritic;
        return new("Routing", ok, s.Plan is null ? "沒有計畫"
            : $"{s.Plan.Strategy} / {s.Plan.WorkflowName}（期望 {c.Expected.Strategy} / {c.Expected.Workflow}）；" +
              $"solver ×{s.Plan.SolverCount}；critic {(s.Plan.IncludeCritic ? "有" : "無")}（期望 {(c.Expected.ExpectCritic ? "有" : "無")}）");
    }

    // 3) Evidence：證據永不進 system prompt、進 user prompt 一律包在中性化區塊、非意見主張必須引用存在的證據
    private static CheckResult Evidence(BenchCase c, CaseState s, TestSystem sys)
    {
        var calls = sys.LlmCalls.ToList();
        var ids = s.Evidence.Select(e => e.Id).ToList();
        bool notInSystem = calls.All(call => ids.All(id => !call.System.Contains(id)));
        bool wrapped = calls.All(call => ids.Where(id => call.User.Contains(id)).All(id => call.User.Contains($"<<<EVIDENCE id={id}")));
        bool cited = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis && !k.IsOpinion)
                             .All(k => k.EvidenceFor.Count > 0 && k.EvidenceFor.All(ids.Contains));
        return new("Evidence", notInSystem && wrapped && cited,
            $"system prompt 無證據 ID：{notInSystem}；中性化區塊：{wrapped}；非意見主張皆有有效引用：{cited}" +
            $"（{s.Claims.Count(k => k.IsOpinion)} 條被標記為意見）");
    }

    // 4) Workflow：所有計畫步驟都有狀態且成功；若中止，必須留下未執行清單
    private static CheckResult Workflow(CaseState s)
    {
        if (s.Plan is null) return new("Workflow", s.StepStatus.IsEmpty, "拒答 → 無步驟執行");
        if (s.Halted)
            return new("Workflow", s.SkippedSteps.Length > 0 && s.SkippedSteps.All(id => !s.StepStatus.ContainsKey(id)),
                $"中止：{s.HaltReason}；未執行 {string.Join(",", s.SkippedSteps)}");
        bool all = s.PlannedSteps.All(id => s.StepStatus.GetValueOrDefault(id) is "ok" or "partial")
                   && s.PlannedSteps.Length == s.StepStatus.Count;
        return new("Workflow", all, $"{s.StepStatus.Count}/{s.PlannedSteps.Length} 步驟完成：" +
            string.Join(" ", s.PlannedSteps.Select(id => $"{id}={s.StepStatus.GetValueOrDefault(id, "—")}")));
    }

    // 5) Verification：能力層 = 實際通過的最高等級；不得超過現場附掛；★ 預登記 hash 必須在 L4 之前寫入且相符
    private static CheckResult Verification(BenchCase c, CaseRun run)
    {
        var s = run.State;
        if (run.Report.Capability is not { } cap)
            return new("Verification", c.Expected.Disposition == Disposition.Abstain, "拒答 → 不輸出能力層");

        var attached = new List<VerifierLevel> { VerifierLevel.L1_LlmCritic, VerifierLevel.L2_Rule };
        if (c.HasL3) attached.Add(VerifierLevel.L3_ExecutableTest);
        if (c.HasL4) attached.Add(VerifierLevel.L4_Experiment);

        bool levelOk = cap.BestPassedLevel == s.BestPassedLevel();
        bool capOk = Math.Abs(cap.Cap - VerifierTrust.Cap(cap.BestPassedLevel)) < 1e-9;
        bool bounded = cap.BestPassedLevel <= attached.Max();

        // ★ 預登記：hash 事件必須在觀察事件之前，且執行後重算仍相符
        var ev = run.Journal.Events.ToList();
        int reg = ev.FindIndex(e => e is ExperimentPreRegistered), obs = ev.FindIndex(e => e is ExperimentObserved);
        bool hashOk = obs < 0 || (reg >= 0 && reg < obs);
        bool hashMatch = s.Experiments.All(e => e.RecomputeHash() == e.LikelihoodHash);

        return new("Verification", levelOk && capOk && bounded && hashOk && hashMatch,
            $"通過最高等級 {cap.BestPassedLevel}（現場最高附掛 {attached.Max()}）→ 上限 {cap.Cap:F2}；" +
            $"L2 {Count(s, VerifierLevel.L2_Rule)}、L1 {Count(s, VerifierLevel.L1_LlmCritic)}、L3 {Count(s, VerifierLevel.L3_ExecutableTest)}、L4 {Count(s, VerifierLevel.L4_Experiment)}" +
            (s.Experiments.Count > 0 ? $"；預登記 hash 先於執行：{hashOk}、比對相符：{hashMatch}" : ""));
    }

    private static string Count(CaseState s, VerifierLevel l)
    {
        var v = s.Verifications.Where(x => x.Level == l).ToList();
        return v.Count == 0 ? "—" : $"{v.Count(x => x.Pass == true)}/{v.Count}";
    }

    // 6) Assurance：三層永遠分開；拒答不輸出數值；★ payload 型別必須與 ReasonCode 相符
    private static CheckResult Assurance(BenchCase c, CaseRun run)
    {
        var r = run.Report;
        bool shapeOk = r.Coverage is null                                        // Phase 1 刻意留空
            && (r.Abstention.Abstained
                  ? r.Capability is null && r.Stability is null
                  : r.Capability is not null && (r.Stability is not null) == (run.State.Decision is not null));
        bool typeOk = r.Abstention.PayloadTypeMatches;

        // payload 缺席只有在「被 L2 退回」時才算正確行為
        bool rejected = run.State.Verifications.Any(v => v.VerifierId == PayloadVerifier.Id && v.Pass == false);
        bool payloadOk = !r.Abstention.Abstained || r.Abstention.Payload is not null || rejected;

        // ★ 獨立稽核：不靠系統自己的 verifier，benchmark 自己再算一次對稱性與可觀察性
        var audit = AuditPayload(r.Abstention.Payload);

        return new("Assurance", shapeOk && typeOk && payloadOk && audit.Ok,
            r.ConservativeGrade() +
            (r.Abstention.Abstained
                ? $"；payload {r.Abstention.Payload?.Kind ?? (rejected ? "被 L2 退回" : "缺")}" +
                  $"（型別綁定 {(typeOk ? "相符" : "錯配")}，引用 {r.Abstention.Payload?.EvidenceRefs.Length ?? 0} 條證據）{audit.Note}"
                : ""));
    }

    /// <summary>benchmark 自己重算一次情境對稱性：系統的 verifier 壞掉時，這裡要抓得到。</summary>
    private static (bool Ok, string Note) AuditPayload(SubstitutePayload? p)
    {
        if (p is not ScenarioBriefing sb || sb.Scenarios.Length < 2) return (p is not ScenarioBriefing, "");
        var ev = sb.Scenarios.Select(x => Math.Max(1, x.Basis.Sum(b => b.EvidenceFor.Length))).ToList();
        var len = sb.Scenarios.Select(x => Math.Max(1, x.Basis.Sum(b => b.Text.Length))).ToList();
        double evR = ev.Max() / (double)ev.Min(), lenR = len.Max() / (double)len.Min();
        bool observable = sb.Scenarios.SelectMany(x => x.Indicators).All(i => i.IsObservable);
        bool ok = evR <= 2.0 && lenR <= 2.5 && observable;
        return (ok, $"；獨立稽核：證據比 {evR:F2}、篇幅比 {lenR:F2}、指標可觀察 {observable}{(ok ? "" : " → 變相方向暗示")}");
    }

    // 7) Permission：稽核事件流，確認沒有任何角色越權寫入
    private static CheckResult Permission(CaseRun run)
    {
        var bad = new List<string>();
        foreach (var e in run.Journal.Events)
        {
            bool ok = e switch
            {
                ClaimProposed { Kind: ClaimKind.Hypothesis } => e.ActorRole == Actors.Solver,
                ClaimProposed { Kind: ClaimKind.Candidate }  => e.ActorRole is Actors.Analyst or Actors.StageSolver,
                ExperimentNominated                          => e.ActorRole == Actors.ExperimentDesigner,
                ExperimentPreRegistered                      => e.ActorRole == Actors.System,   // ★ 登記只能是確定性程式
                VerificationRecorded v                       => v.Result.Level == VerifierLevel.L1_LlmCritic ? e.ActorRole == Actors.Critic : e.ActorRole == Actors.Machine,
                UtilityMatrixSet or HumanActed               => e.ActorRole == Actors.Human,
                BeliefUpdated or DecisionMade                => e.ActorRole == Actors.System,
                _ => true
            };
            if (!ok) bad.Add($"{e.GetType().Name}←{e.ActorRole}");
        }
        int llmWrites = run.Journal.Events.Count(e => Actors.IsLlmRole(e.ActorRole));
        return new("Permission", bad.Count == 0,
            bad.Count == 0 ? $"{run.Journal.Events.Count} 筆事件全部在授權車道內（其中 {llmWrites} 筆由 LLM 角色寫入：主張 / 實驗提名 / L1 批判）"
                           : "越權：" + string.Join(", ", bad));
    }

    // 8) ★ Canonicalization：同四元組不同措辭必須得到同一個 canonical id（MR-14 的逐案版）
    private static CheckResult Canonicalization(BenchCase c, CaseRun run)
    {
        if (c.Expected.Disposition == Disposition.Abstain)
            return new("Canonicalization", run.State.Claims.IsEmpty, "拒答 → 無主張需要對映");

        var canon = run.Journal.Events.OfType<ClaimCanonicalized>().ToList();
        var byLocal = canon.GroupBy(e => e.LocalId).ToList();

        // (a) 一致性：同一個主張的每一筆提名都必須對到同一個 id（換句話說不該產生新 id）
        var inconsistent = byLocal.Where(g => g.Select(x => x.CatalogId).Distinct().Count() > 1).ToList();

        // (b) 跨 case 記憶：種子裡有的機制，必須命中 Catalog 而不是變成新條目
        var seededIdentities = c.CatalogSeed.Select(f => f.Identity).ToHashSet();
        var shouldHit = run.State.Claims.Where(k => seededIdentities.Contains(k.Frame.Identity)).ToList();
        var missed = shouldHit.Where(k => !k.Key.IsCatalogued).ToList();

        int proposals = run.Journal.Events.OfType<ClaimProposed>().Count(e => e.Kind == ClaimKind.Hypothesis);
        int claims = run.State.Claims.Count(k => k.Kind == ClaimKind.Hypothesis);
        int hits = run.State.Claims.Count(k => k.Key.IsCatalogued);

        bool ok = inconsistent.Count == 0 && missed.Count == 0;
        string detail =
            inconsistent.Count > 0
                ? $"同一主張得到多個 catalog id：{string.Join("、", inconsistent.Select(g => $"{g.Key}→{string.Join("/", g.Select(x => x.CatalogId))}"))}"
            : missed.Count > 0
                ? $"Catalog 種子有 {c.CatalogSeed.Length} 條，但 {string.Join("、", missed.Select(k => k.LocalId))} 沒有命中 → 跨 case 記憶失效"
                : $"{proposals} 筆提名合併成 {claims} 條主張；種子 {c.CatalogSeed.Length} 條，命中 {hits} 條；" +
                  string.Join("　", byLocal.Select(g => $"{g.Key}:{g.First().Match}"));
        return new("Canonicalization", ok, detail);
    }

    // 9) ★ Independence：降級等級必須由家族數決定，且誠實反映在 Assurance
    private static CheckResult Independence(BenchCase c, CaseRun run)
    {
        var s = run.State;
        if (s.Budget is null)
            return new("Independence", c.Expected.Disposition == Disposition.Abstain, "拒答 → 未清點獨立性");

        var expected = IndependenceBudget.LevelFor(s.Budget.DistinctFamilies, s.Roles.Length > 0 ? Math.Max(s.Roles.Length, s.Budget.DistinctFamilies) : s.Budget.DistinctFamilies);
        bool levelOk = s.Degradation == expected || s.Degradation == IndependenceBudget.LevelFor(s.Budget.DistinctFamilies, 5);
        bool honest = s.Budget.EnsembleClaimsPermitted
                      || run.Report.Capability is null
                      || run.Report.Capability.Degraded;      // 不准宣稱集成增益時，能力層必須降級
        bool solversCapped = s.Plan is null || s.Plan.SolverCount == Degradation.CapSolvers(s.Degradation, s.Plan.SolverCount);

        return new("Independence", levelOk && honest && solversCapped,
            $"{s.Budget.Describe()}；降級 {s.Degradation}；solver ×{s.Plan?.SolverCount ?? 0}" +
            (run.Report.Capability is { Degraded: true } cap ? $"；能力層已降級（{string.Join("；", cap.DegradedReasons)}）" : ""));
    }

    // 10) ☆ Stability：三擾動必須分開回報，而且每個數字都要能被獨立重算推翻
    private static CheckResult Stability(BenchCase c, CaseRun run)
    {
        var s = run.State;
        var st = run.Report.Stability;
        var resampled = run.Journal.Events.OfType<StabilityResampled>().LastOrDefault();
        int solvers = s.AgentsUsedAs("solver").Count();

        // (a) 沒有決策、或只有一個 solver → 沒有樣本可抽，必須回報 0 次。
        //     宣稱跑了 25 次全穩，是無中生有的穩定度。
        bool trialsHonest = resampled is null || (solvers >= 2 && s.Decision is not null
            ? resampled.Trials > 0 : resampled.Trials == 0);
        if (!trialsHonest)
            return new("Stability", false,
                $"{(s.Decision is null ? "沒有決策" : $"只有 {solvers} 個 solver")}卻回報跑了 {resampled!.Trials} 次重抽樣 —— 憑空的穩定度");

        if (s.Decision is null)
            return new("Stability", st is null, st is null ? "沒有決策 → 沒有穩定層" : "沒有決策卻有穩定層");

        bool rangeOk = st!.Robustness is >= 0 and <= 1
                       && (st.DecisionStabilityUnderResampling is null or >= 0 and <= 1);
        bool baselineOk = resampled is null || resampled.BaselineAction == (s.Decision.RecommendedAction ?? "");

        // (b) 獨立重算：宣稱 100 % 穩定的話，少掉任何一個 solver 都不該改變推薦行動。
        //     這是最便宜的必要條件——連它都過不了，100 % 就是假的。
        var flipped = new List<string>();
        if (st.DecisionStabilityUnderResampling is >= 1.0 && solvers >= 2)
            foreach (var agent in s.AgentsUsedAs("solver").OrderBy(x => x, StringComparer.Ordinal))
            {
                string? action = DecideWithout(s, agent);
                if (action is not null && action != s.Decision.RecommendedAction) flipped.Add($"少了 {agent} → 改推薦 {action}");
            }

        // (c) 似然敏感度：說「首位是誰」就必須跟實際後驗一致；沒做分析時不得宣稱穩定
        var sens = run.Journal.Events.OfType<SensitivityAssessed>().LastOrDefault();
        string? posteriorTop = s.Beliefs.Count == 0 ? null
            : s.Beliefs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        bool observedExperiment = s.Experiments.Any(e => e.ObservedOutcome is not null);
        bool sensHonest = !observedExperiment
            ? sens is null && st.PosteriorOrderStableUnderLikertShift is null
            : sens is not null && (sens.OrderChanged || sens.TopBaseline == posteriorTop);

        // (b2) 宣稱的比例必須可被獨立重算出同一個值。穩定度是列舉算出來的，不是抽樣，
        //      所以這裡可以要求「完全一致」而不是「差不多」——差不多就是留一條可以寫假數字的縫。
        string? unreproducible = null;
        if (st.DecisionStabilityUnderResampling is { } claimed && solvers >= 2)
        {
            var recomputed = new DecisionStabilityAnalyzer(new CalibratedEnsembler(), new TemperedBayesUpdater(), new DecisionEngine())
                .Analyze(s, PolicySnapshot.Initial, new DecisionAI.Adapters.Runtime.SeededRandomSource(20260918));
            int claimedTrials = resampled?.Trials ?? 0;
            if (recomputed.Trials > 0 && Math.Abs(claimed - recomputed.StabilityRate) > 1e-6)
                unreproducible = $"宣稱重抽樣穩定度 {claimed:P0}，獨立重算得到 {recomputed.StabilityRate:P0}" +
                                 $"（{recomputed.Detail}）→ 這個數字不是量出來的";
            // 樣本數同樣要對得上：列舉法的子集數是確定的，寫別的數字就是沒有真的列舉
            else if (recomputed.Trials > 0 && claimedTrials != recomputed.Trials)
                unreproducible = $"宣稱評估了 {claimedTrials} 個樣本，但 {solvers} 個 solver 的非空子集只有 {recomputed.Trials} 個可用 " +
                                 "→ 這個穩定度不是列舉出來的";
        }

        string detail = $"機率擾動 {st.Robustness:P0}；" +
            $"假設集重抽樣 {(st.DecisionStabilityUnderResampling is { } q ? $"{q:P0}（{resampled?.Trials ?? 0} 次，{solvers} 個 solver）" : "n/a")}；" +
            $"似然 ±1 級{(st.PosteriorOrderStableUnderLikertShift switch { true => "排序不變", false => "排序會變 → 已掛 LikelihoodSensitive", _ => " 未分析" })}";
        if (flipped.Count > 0) detail = $"宣稱 100 % 穩定，但 leave-one-out 就翻了：{string.Join("；", flipped)}";
        else if (unreproducible is not null) detail = unreproducible;
        else if (!sensHonest) detail = sens is null
            ? "有已觀察的實驗卻沒有做似然敏感度分析"
            : $"宣告首位 {sens.TopBaseline ?? "（無）"} 與實際後驗首位 {posteriorTop} 不一致";
        else if (!baselineOk) detail = "重抽樣的基準行動與實際推薦不一致";

        return new("Stability", rangeOk && baselineOk && sensHonest && flipped.Count == 0 && unreproducible is null, detail);
    }

    /// <summary>拿掉某個 solver 的提案後重算一次決策。這裡刻意用好的引擎重算，不讀事件流裡的數字。</summary>
    private static string? DecideWithout(CaseState s, string agent)
    {
        var claims = s.Claims
            .Select(k => k with { Proposals = k.Proposals.Where(p => p.AgentId != agent).ToImmutableList() })
            .Where(k => k.Kind != ClaimKind.Hypothesis || k.Proposals.Count > 0).ToImmutableList();
        var trimmed = s with { Claims = claims, AgentRuns = s.AgentRuns.Where(r => r.AgentId != agent).ToImmutableList() };
        if (!trimmed.Hypotheses.Any()) return null;

        var prior = new CalibratedEnsembler().Prior(trimmed, PolicySnapshot.Initial);
        if (prior.Count == 0) return null;
        var bayes = new TemperedBayesUpdater();
        foreach (var exp in trimmed.Experiments.Where(e => e.ObservedOutcome is not null)) prior = bayes.Update(prior, exp);

        return new DecisionEngine().Decide(prior, trimmed.AlignedUtilities,
            trimmed.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity),
            new DecisionAI.Adapters.Runtime.SeededRandomSource(4242)).RecommendedAction;
    }

    // 11) ☆ Saturation：Chao1 必須被算出來，覆蓋率低時能力層必須跟著降級
    private static CheckResult Saturation(BenchCase c, CaseRun run)
    {
        var s = run.State;
        if (c.Expected.Disposition == Disposition.Abstain)
            return new("Saturation", s.Saturation is null, s.Saturation is null ? "拒答 → 未估計飽和度" : "拒答卻估了飽和度");
        if (s.Plan?.Strategy == Strategy.MultiDomainPipeline)
            return new("Saturation", true, "流水線題型沒有假設空間可飽和（交付物不是假設）");

        if (s.Saturation is not { } est) return new("Saturation", false, "進入流程卻沒有 SaturationEstimated 事件");

        // 獨立重算：check 自己數一次 singleton / doubleton，不讀它寫的數字
        var hyps = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis).ToList();
        int f1 = hyps.Count(k => k.Proposals.Select(p => p.AgentId).Distinct().Count() == 1);
        int f2 = hyps.Count(k => k.Proposals.Select(p => p.AgentId).Distinct().Count() == 2);
        bool countsOk = est.Singletons == f1 && est.Doubletons == f2;

        double coverage = Chao1SaturationEstimator.Coverage(s.Hypotheses.Count(), est);
        bool honest = coverage >= 0.60 || run.Report.Capability is null || run.Report.Capability.Degraded;

        return new("Saturation", countsOk && honest,
            !countsOk ? $"飽和度數字與事件流不符（事件 {est.Singletons}/{est.Doubletons}，實際 {f1}/{f2}）"
            : !honest ? $"覆蓋率僅 {coverage:P0} 卻沒有降級能力層"
            : $"singletons {f1}、doubletons {f2}、估計未發現 {est.EstimatedUndiscovered:F1} → 覆蓋率 {coverage:P0}");
    }

    // 12) ☆ Coverage：沒有校準樣本就不得出現覆蓋層；白名單外題型永遠不得有
    private static CheckResult Coverage(BenchCase c, CaseRun run)
    {
        string family = TaskFamilyClassifier.Classify(run.State.Facts);
        bool whitelisted = TaskFamilies.Whitelist.Contains(family);
        var predicted = run.Journal.Events.OfType<CoveragePredicted>().ToList();

        // 這批案例都沒有累積校準樣本 → 正確行為是「一個覆蓋層都不給」
        bool ok = predicted.Count == 0 && run.Report.Coverage is null;
        string detail = ok
            ? $"題型 {family}（{(whitelisted ? "在白名單" : "白名單外")}）；沒有校準樣本 → 不給覆蓋層"
            : !whitelisted
                ? $"題型 {family} 不在白名單（可交換性被破壞）卻給了覆蓋保證"
                : $"沒有任何校準樣本卻宣稱 {run.Report.Coverage?.TargetCoverage:P0} 覆蓋 → 假保證";
        return new("Coverage", ok, detail);
    }

    // 13) ☆ ExperimentSelection：EVOI 由 check 自己重算，登記的必須有正的 EVOI
    private static CheckResult ExperimentSelection(BenchCase c, CaseRun run)
    {
        var nominated = run.Journal.Events.OfType<ExperimentNominated>().ToList();
        if (nominated.Count == 0)
            return new("ExperimentSelection", run.State.Experiments.IsEmpty,
                run.State.Experiments.IsEmpty ? "沒有實驗提名" : "沒有提名卻出現登記的實驗");

        var registered = run.State.Experiments.ToList();
        var skipped = run.Journal.Events.OfType<ExperimentSkipped>().ToList();
        bool accounted = registered.Count + skipped.Count >= nominated.Count;

        // 用登記前的信念獨立重算 EVOI
        var beliefsAtRegistration = BeliefsBefore(run, typeof(ExperimentPreRegistered));
        var selector = new EvoiExperimentSelector();
        var bad = new List<string>();
        foreach (var exp in registered)
        {
            var choice = selector.Select(
                new[] { (exp.Id, exp.Outcomes, exp.LikelihoodByClaim, 0.0) },
                beliefsAtRegistration, run.State.AlignedUtilities);
            if (choice.Run.IsEmpty) bad.Add($"{exp.Id} 的 EVOI ≤ 0 卻被登記（{choice.Skip.FirstOrDefault()?.Detail}）");
        }

        return new("ExperimentSelection", bad.Count == 0 && accounted,
            bad.Count > 0 ? string.Join("；", bad)
            : !accounted ? $"提名 {nominated.Count} 個，登記 {registered.Count} + 跳過 {skipped.Count} → 有提名不知去向"
            : $"提名 {nominated.Count} 個 → 登記 {registered.Count}、跳過 {skipped.Count}" +
              (skipped.Count > 0 ? $"（跳過理由：{skipped[0].Reason}）" : ""));
    }

    // 14) ☆ Ensemble：相關性折扣必須真的被套上，而且寫進事件流可稽核
    private static CheckResult Ensemble(BenchCase c, CaseRun run)
    {
        var s = run.State;
        int solvers = s.AgentsUsedAs("solver").Count();
        var lines = s.Log.Where(l => l.Contains("集成 ")).ToList();

        if (solvers < 2 || s.Beliefs.Count == 0)
            return new("Ensemble", true, solvers < 2 ? "只有一個 solver → 沒有集成可折扣" : "沒有信念分布");

        bool logged = lines.Count >= solvers;
        // 任兩個模型之間的相關性都不可能是 0：同廠商、同家族、甚至同一批語料
        bool discounted = lines.Any(l => l.Contains("÷ (1+") && !l.Contains("÷ (1+0.00)"));

        return new("Ensemble", logged && discounted,
            !logged ? $"{solvers} 個 solver 但事件流只有 {lines.Count} 筆集成理由 → 權重無法稽核"
            : !discounted ? "所有 agent 的相關性折扣都是 0 → 把同源的一致答案當成獨立證據"
            : $"{solvers} 個 solver 的權重與折扣都已寫進事件流（{lines.Count} 筆）");
    }

    /// <summary>事件流裡在某型別事件第一次出現之前的最後一組信念。</summary>
    private static IReadOnlyDictionary<string, double> BeliefsBefore(CaseRun run, Type marker)
    {
        var beliefs = (IReadOnlyDictionary<string, double>)ImmutableDictionary<string, double>.Empty;
        foreach (var e in run.Journal.Events)
        {
            if (e.GetType() == marker) break;
            if (e is BeliefUpdated b) beliefs = b.Posterior;
        }
        return beliefs;
    }

    public static string RenderBeliefs(CaseState s) => s.Beliefs.Count == 0 ? "" : Beliefs.Render(s.Beliefs);
}
