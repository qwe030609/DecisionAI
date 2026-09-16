// ============================================================================
//  ChatGPT 版為每個案例宣告的「禁止行為」。原檔宣告 35 種，evaluate() 只真的檢查 1 種。
//  這裡照抄，用來稽核：Phase 1 究竟結構上阻止了哪些、哪些只是「這次沒發生」。
// ============================================================================

namespace DecisionAI.Benchmark;

public static class GptForbidden
{
    private static readonly Dictionary<string, string[]> Map = new()
    {
        ["V1"] = new[] { "opinion_only" },
        ["V2"] = new[] { "single_cause_without_test" },
        ["V3"] = new[] { "true_false_verdict" },
        ["D1"] = new[] { "single_agent_monolith" },
        ["D2"] = new[] { "guess_without_trace" },
        ["D3"] = new[] { "unnecessary_multi_agent" },
        ["U1"] = new[] { "ensemble" },
        ["U2"] = new[] { "uncalibrated_confidence" },
        ["U3"] = new[] { "majority_vote", "single_point_prediction" },
        ["S1"] = new[] { "opinion_poll" },
        ["S2"] = new[] { "taste_only" },
        ["S3"] = new[] { "claim_correct_answer" },
        ["B1"] = new[] { "unnecessary_domain_fanout" },
        ["B2"] = new[] { "software_only_diagnosis" },
        ["B3"] = new[] { "single_domain_plan" },
        ["C1"] = new[] { "idea_spam" },
        ["C2"] = new[] { "single_option" },
        ["C3"] = new[] { "truth_verifier", "single_candidate" },
        ["T1"] = new[] { "fresh_web_required" },
        ["T2"] = new[] { "timeless_assumption" },
        ["T3"] = new[] { "stale_evidence", "point_prediction" },
        ["R1"] = new[] { "expensive_workflow" },
        ["R2"] = new[] { "preference_only" },
        ["R3"] = new[] { "auto_continue_production" },
        ["X1"] = new[] { "unverified_heuristic" },
        ["X2"] = new[] { "judge_answer" },
        ["X3"] = new[] { "majority_vote", "auto_trade" },
        ["X4"] = new[] { "independent_outputs_without_integration" },
        ["X5"] = new[] { "truth_verifier" },
        ["X6"] = new[] { "high_uncertainty_equals_high_temporality" },
        ["X7"] = new[] { "forecast_ensemble", "automatic_release" },
        ["X8"] = new[] { "human_taste_as_verifier" },
    };

    public static string[] For(string id) => Map.GetValueOrDefault(id, Array.Empty<string>());
}
