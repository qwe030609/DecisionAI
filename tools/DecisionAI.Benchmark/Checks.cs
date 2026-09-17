// ============================================================================
//  九項檢查 —— 全部可被推翻（falsifiable）。
//  對照組（ChatGPT 版）的 Profile 檢查是 profile ∈ profile±0.1，恆真；
//  Output 檢查等於 Routing 的推論。這裡每一項都由「宣告的金標」與「真實事件流」比對。
//  ★ 為 Rev2 新增的檢查。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Modules.Assurance;
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
            Verification(c, run), Assurance(c, run), Permission(run), Canonicalization(c, run), Independence(c, run));

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

    public static string RenderBeliefs(CaseState s) => s.Beliefs.Count == 0 ? "" : Beliefs.Render(s.Beliefs);
}
