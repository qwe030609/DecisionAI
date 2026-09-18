// ============================================================================
//  禁止行為稽核：哪些是結構上做不到的，哪些只是「這次沒發生」。
//  對照組宣告 35 種但只真的檢查 1 種；這裡逐條說明依據。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Orchestration;

namespace DecisionAI.Benchmark;

public static class ForbiddenAudit
{
    public static ImmutableArray<(string Behavior, string Status, string Why)> Run(
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
        bool delegated = run.Report.Abstention.Code == Core.Assurance.ReasonCode.DelegatedToExternalModel;
        bool hashed = s.Experiments.All(e => e.RecomputeHash() == e.LikelihoodHash);

        (string, string) Verdict(string b) => b switch
        {
            "opinion_only" or "single_cause_without_test" or "guess_without_trace" or "unverified_heuristic"
              or "taste_only" or "preference_only"
                => (l3Passed ? "結構阻止" : delegated ? "結構阻止" : "未覆蓋",
                    l3Passed ? "有 L3 以上通過紀錄，結論不是意見"
                    : delegated ? "轉介專業工具，系統沒有自己下結論" : "本案沒有 L3 通過紀錄"),

            "true_false_verdict" or "claim_correct_answer" or "judge_answer" or "opinion_poll"
                => (abstained ? "結構阻止" : "未覆蓋",
                    abstained ? $"拒答（{run.Report.Abstention.Code}），payload 型別是 {run.Report.Abstention.Payload?.Kind}，型別上產不出裁決" : "本案有輸出結論"),

            "single_point_prediction" or "point_prediction" or "stale_evidence" or "forecast_ensemble"
                => (abstained ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答時連機率都不輸出；ScenarioBriefing 的型別裡沒有機率欄位" : "本案有輸出分布"),

            "auto_trade" or "auto_continue_production" or "automatic_release"
                => (abstained || humanRan ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答，不可能自動執行" : humanRan ? "人類閘實際執行過，事件流有 HumanActed" : "沒有人類閘"),

            "unnecessary_multi_agent" or "ensemble" or "unnecessary_domain_fanout" or "expensive_workflow"
                => (solvers == 1 || abstained ? "結構阻止" : "未覆蓋",
                    abstained ? "根本沒有進入流程" : solvers == 1 ? "計畫只用 1 個 solver" : $"用了 {solvers} 個 solver"),

            "single_agent_monolith" or "single_domain_plan" or "independent_outputs_without_integration"
                => (piped && gates >= 2 ? "結構阻止" : "未覆蓋",
                    piped ? $"流水線 + {gates} 道人類邊界閘，介面契約沒過不往下游傳" : "非流水線"),

            "majority_vote"
                => (abstained || s.Beliefs.Count > 0 ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答" : "集成用加權 log-odds，不是計票；未提假設者算弱反對票"),

            "uncalibrated_confidence"
                => ("結構阻止",
                    abstained ? "拒答時不輸出信心" : delegated ? "轉介外部模型" :
                    $"信心上限由驗證等級決定（{run.Report.Capability?.Cap:F2}）；自報信心是 Likert 等級，由確定性查表轉成機率"),

            "truth_verifier" or "human_taste_as_verifier"
                => (abstained || l3Passed ? "結構阻止" : "未覆蓋",
                    abstained ? "沒有宣稱任何 verifier" : "verifier 是可執行測試，不是人的品味"),

            "high_uncertainty_equals_high_temporality"
                => (delegated ? "結構阻止" : !abstained ? "結構阻止" : "未覆蓋",
                    delegated ? "不確定性高但有成熟的統計模型 → 轉介，而不是當成不可預測"
                              : "不確定性高但有 holdout 可驗證 → 仍然可做"),

            "single_option" or "single_candidate"
                => (s.Claims.Count >= 2 || abstained ? "結構阻止" : "未覆蓋",
                    abstained ? "拒答時給的是考量地圖，本來就是多選項" : $"產生 {s.Claims.Count} 條主張"),

            "idea_spam"
                => (s.Claims.Count <= 5 ? "結構阻止" : "未覆蓋", $"{s.Claims.Count} 條主張，沒有灌水"),

            "software_only_diagnosis"
                => (sourceKinds >= 2 ? "結構阻止" : "未覆蓋", $"證據橫跨 {sourceKinds} 種來源類型"),

            "fresh_web_required" or "timeless_assumption"
                => ("未覆蓋", "Phase 1 沒有證據新鮮度政策（Evidence Allocator / 漂移告警在 Phase 2-3）"),

            _ => ("未覆蓋", "Phase 1 無對應機制")
        };

        return behaviors.Select(b =>
        {
            var (st, why) = Verdict(b);
            // 預登記 hash 是 Rev2 新增的結構保證，對「事後解釋」這一類行為補一句
            if (b is "single_cause_without_test" && s.Experiments.Count > 0)
                why += $"；實驗似然在執行前登記並比對 hash（{(hashed ? "相符" : "不符")}）";
            return (b, st, why);
        }).ToImmutableArray();
    }
}
