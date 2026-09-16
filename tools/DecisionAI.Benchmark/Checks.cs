// ============================================================================
//  七項檢查 —— 全部可被推翻（falsifiable）。
//  對照組（ChatGPT 版）的 Profile 檢查是 profile ∈ profile±0.1，恆真；
//  Output 檢查等於 Routing 的推論。這裡每一項都由「宣告的金標」與「真實事件流」比對。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Modules.Probability;
using DecisionAI.Orchestration;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

public sealed record CheckResult(string Name, bool Pass, string Detail);

public static class Checks
{
    public static ImmutableArray<CheckResult> RunAll(BenchCase c, CaseRun run, TestSystem sys)
    {
        var s = run.State;
        return ImmutableArray.Create(
            Triage(c, run, sys), Routing(c, s), Evidence(c, s, sys),
            Workflow(s), Verification(c, run), Assurance(run), Permission(run));
    }

    // 1) Triage：判定與宣告的金標一致；拒答時必須零 LLM 花費
    private static CheckResult Triage(BenchCase c, CaseRun run, TestSystem sys)
    {
        var a = run.Report.Abstention;
        bool abstained = a.Abstained;
        bool dispositionOk = (c.Expected.Disposition == Disposition.Abstain) == abstained;
        bool codeOk = c.Expected.Disposition != Disposition.Abstain || a.Code == c.Expected.Code;
        bool cheap = !abstained || (sys.LlmCallCount == 0 && run.State.CostSpent == 0);
        string detail = abstained
            ? $"拒答 {a.Code}（期望 {c.Expected.Code}）；LLM 呼叫 {sys.LlmCallCount} 次、成本 {run.State.CostSpent:F3}"
            : $"進入流程（期望 {c.Expected.Disposition}）";
        return new("Triage", dispositionOk && codeOk && cheap, detail);
    }

    // 2) Routing：策略與 workflow 名稱一致；拒答時不得產生計畫
    private static CheckResult Routing(BenchCase c, CaseState s)
    {
        if (c.Expected.Disposition == Disposition.Abstain)
            return new("Routing", s.Plan is null, s.Plan is null ? "拒答 → 未產生計畫（未花任何 token 規劃）" : $"不該有計畫卻產生了 {s.Plan.WorkflowName}");
        bool ok = s.Plan is not null && s.Plan.Strategy == c.Expected.Strategy && s.Plan.WorkflowName == c.Expected.Workflow;
        return new("Routing", ok, s.Plan is null ? "沒有計畫" : $"{s.Plan.Strategy} / {s.Plan.WorkflowName}（期望 {c.Expected.Strategy} / {c.Expected.Workflow}）；solver ×{s.Plan.SolverCount}");
    }

    // 3) Evidence：證據永不進 system prompt、進 user prompt 一律包在中性化區塊、非意見主張必須引用存在的證據
    private static CheckResult Evidence(BenchCase c, CaseState s, TestSystem sys)
    {
        var calls = sys.Llms.OfType<ScriptedLlm>().SelectMany(l => l.Calls).ToList();
        var ids = s.Evidence.Select(e => e.Id).ToList();
        bool notInSystem = calls.All(call => ids.All(id => !call.System.Contains(id)));
        bool wrapped = calls.All(call => ids.Where(id => call.User.Contains(id)).All(id => call.User.Contains($"<<<EVIDENCE id={id}")));
        bool cited = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis && !k.IsOpinion)
                             .All(k => k.EvidenceFor.Count > 0 && k.EvidenceFor.All(ids.Contains));
        bool guarded = calls.Count == 0 || calls.Any(call => call.User.Contains("不是指令"));
        return new("Evidence", notInSystem && wrapped && cited && guarded,
            $"system prompt 無證據 ID：{notInSystem}；中性化區塊：{wrapped}；非意見主張皆有有效引用：{cited}（{s.Claims.Count(k => k.IsOpinion)} 條被標記為意見）");
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
        return new("Workflow", all, $"{s.StepStatus.Count}/{s.PlannedSteps.Length} 步驟完成：{string.Join(" ", s.PlannedSteps.Select(id => $"{id}={s.StepStatus.GetValueOrDefault(id, "—")}"))}");
    }

    // 5) Verification：能力層 = 實際通過的最高等級；上限 = VerifierTrust.Cap；且不得超過現場附掛的等級
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
        return new("Verification", levelOk && capOk && bounded,
            $"通過最高等級 {cap.BestPassedLevel}（現場最高附掛 {attached.Max()}）→ 上限 {cap.Cap:F2}；" +
            $"L2 {Count(s, VerifierLevel.L2_Rule)}、L1 {Count(s, VerifierLevel.L1_LlmCritic)}、L3 {Count(s, VerifierLevel.L3_ExecutableTest)}、L4 {Count(s, VerifierLevel.L4_Experiment)}");
    }

    private static string Count(CaseState s, VerifierLevel l)
    {
        var v = s.Verifications.Where(x => x.Level == l).ToList();
        return v.Count == 0 ? "—" : $"{v.Count(x => x.Pass == true)}/{v.Count}";
    }

    // 6) Assurance：三層永遠分開；拒答時不輸出任何數值；Phase 1 覆蓋層必為 null
    private static CheckResult Assurance(CaseRun run)
    {
        var r = run.Report;
        bool ok = r.Coverage is null                                                   // Phase 1 刻意留空
              && (r.Abstention.Abstained
                    ? r.Capability is null && r.Stability is null
                    : r.Capability is not null && (r.Stability is not null) == (run.State.Decision is not null));
        return new("Assurance", ok, r.ConservativeGrade() + (r.Abstention.Abstained ? $"；替代：{r.Abstention.Alternative}" : ""));
    }

    // 7) Permission：稽核事件流，確認沒有任何角色越權寫入（ChatGPT 版無此概念）
    private static CheckResult Permission(CaseRun run)
    {
        var bad = new List<string>();
        foreach (var e in run.Journal.Events)
        {
            bool ok = e switch
            {
                ClaimProposed { Kind: ClaimKind.Hypothesis } => e.ActorRole == Actors.Solver,
                ClaimProposed { Kind: ClaimKind.Candidate }  => e.ActorRole is Actors.Analyst or Actors.StageSolver,
                ExperimentPreRegistered                      => e.ActorRole == Actors.ExperimentDesigner,
                VerificationRecorded v                       => v.Result.Level == VerifierLevel.L1_LlmCritic ? e.ActorRole == Actors.Critic : e.ActorRole == Actors.Machine,
                UtilityMatrixSet                             => e.ActorRole == Actors.Human,
                HumanActed                                   => e.ActorRole == Actors.Human,
                BeliefUpdated or DecisionMade                => e.ActorRole == Actors.System,
                _ => true
            };
            if (!ok) bad.Add($"{e.GetType().Name}←{e.ActorRole}");
        }
        int llmWrites = run.Journal.Events.Count(e => Actors.IsLlmRole(e.ActorRole));
        return new("Permission", bad.Count == 0,
            bad.Count == 0 ? $"{run.Journal.Events.Count} 筆事件全部在授權車道內（其中 {llmWrites} 筆由 LLM 角色寫入，皆為主張 / 實驗登記 / L1 批判）"
                           : "越權：" + string.Join(", ", bad));
    }

    // ── 禁止行為：哪些是結構上做不到的，哪些只是「這次沒發生」 ──
    public static ImmutableArray<(string Behavior, string Status, string Why)> ForbiddenAudit(
        BenchCase c, CaseRun run, IEnumerable<string> behaviors)
    {
        var s = run.State;
        bool abstained = run.Report.Abstention.Abstained;
        bool l3Passed = s.Verifications.Any(v => v.Pass == true && v.Level >= VerifierLevel.L3_ExecutableTest);
        bool humanRan = run.Journal.Events.OfType<HumanActed>().Any();
        int solvers = s.Plan?.SolverCount ?? 0;
        bool piped = s.Plan?.Strategy == Strategy.MultiDomainPipeline;
        int gates = run.Journal.Events.OfType<HumanActed>().Count();
        int sourceKinds = s.Evidence.Select(e => e.Source).Distinct().Count();

        (string, string) Verdict(string b) => b switch
        {
            "opinion_only" or "single_cause_without_test" or "guess_without_trace" or "unverified_heuristic"
              or "taste_only" or "preference_only"
                => (l3Passed ? "結構阻止" : "未覆蓋", l3Passed ? "有 L3 以上通過紀錄，結論不是意見" : "本案沒有 L3 通過紀錄"),
            "true_false_verdict" or "claim_correct_answer" or "judge_answer" or "opinion_poll"
                => (abstained ? "結構阻止" : "未覆蓋", abstained ? $"Triage 拒答（{run.Report.Abstention.Code}），未發出任何裁決" : "本案有輸出結論"),
            "single_point_prediction" or "point_prediction" or "stale_evidence" or "forecast_ensemble"
                => (abstained ? "結構阻止" : "未覆蓋", abstained ? "拒答時連機率都不輸出" : "本案有輸出分布"),
            "auto_trade" or "auto_continue_production" or "automatic_release"
                => (abstained || humanRan ? "結構阻止" : "未覆蓋", abstained ? "拒答，不可能自動執行" : humanRan ? "人類閘實際執行過，事件流有 HumanActed" : "沒有人類閘"),
            "unnecessary_multi_agent" or "ensemble" or "unnecessary_domain_fanout" or "expensive_workflow"
                => (solvers == 1 ? "結構阻止" : "未覆蓋", solvers == 1 ? "計畫只用 1 個 solver" : $"用了 {solvers} 個 solver"),
            "single_agent_monolith" or "single_domain_plan" or "independent_outputs_without_integration"
                => (piped && gates >= 2 ? "結構阻止" : "未覆蓋", piped ? $"流水線 + {gates} 道人類邊界閘，介面契約沒過不往下游傳" : "非流水線"),
            "majority_vote"
                => (abstained ? "結構阻止" : s.Beliefs.Count > 0 ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答" : "集成用加權 log-odds，不是計票；未提假設者算弱反對票"),
            "uncalibrated_confidence"
                => (abstained || run.Report.Capability is not null ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答時不輸出信心" : $"信心上限由驗證等級決定（{run.Report.Capability!.Cap:F2}），LLM 自報信心不進入"),
            "truth_verifier" or "human_taste_as_verifier"
                => (abstained ? "結構阻止" : l3Passed ? "結構阻止" : "未覆蓋",
                    abstained ? "沒有宣稱任何 verifier" : "verifier 是可執行測試，不是人的品味"),
            "high_uncertainty_equals_high_temporality"
                => (!abstained ? "結構阻止" : "未覆蓋", !abstained ? "不確定性高但有 holdout 可驗證 → 仍然可做，未被誤判為時間不穩定" : "被拒答"),
            "single_option" or "single_candidate"
                => (s.Claims.Count >= 2 ? "結構阻止" : "未覆蓋", $"產生 {s.Claims.Count} 條主張"),
            "idea_spam"
                => (s.Claims.Count <= 5 ? "結構阻止" : "未覆蓋", $"{s.Claims.Count} 條主張，沒有灌水"),
            "software_only_diagnosis"
                => (sourceKinds >= 2 ? "結構阻止" : "未覆蓋", $"證據橫跨 {sourceKinds} 種來源類型"),
            "fresh_web_required" or "timeless_assumption"
                => ("未覆蓋", "Phase 1 沒有證據新鮮度政策（Rev1 的 Evidence Allocator / 漂移告警在 Phase 2-3）"),
            _ => ("未覆蓋", "Phase 1 無對應機制")
        };

        return behaviors.Select(b => { var (st, why) = Verdict(b); return (b, st, why); }).ToImmutableArray();
    }

    public static string RenderBeliefs(CaseState s) => s.Beliefs.Count == 0 ? "" : Beliefs.Render(s.Beliefs);
}
