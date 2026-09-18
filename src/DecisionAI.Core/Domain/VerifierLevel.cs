// ============================================================================
//  驗證器等級 — 能力層上限的唯一來源。LLM 自報信心永遠不進這裡。
// ============================================================================

namespace DecisionAI.Core.Domain;

public enum VerifierLevel
{
    None              = 0,   // 什麼都沒通過
    L1_LlmCritic      = 1,   // 另一個 LLM 說「看起來對」
    L2_Rule           = 2,   // 確定性規則：引用存在、格式、範圍
    L3_ExecutableTest = 3,   // 編譯 / 單元測試 / property test / 模擬器
    L4_Experiment     = 4,   // 真實系統上的實驗（HIL、staging、A/B）
    L5_RealOutcome    = 5    // 部署後的真實結果
}

public static class VerifierTrust
{
    /// <summary>能力層上限由「最高已通過的驗證器等級」決定。</summary>
    public static double Cap(VerifierLevel l) => l switch
    {
        VerifierLevel.L1_LlmCritic      => 0.60,
        VerifierLevel.L2_Rule           => 0.70,
        VerifierLevel.L3_ExecutableTest => 0.90,
        VerifierLevel.L4_Experiment     => 0.95,
        VerifierLevel.L5_RealOutcome    => 1.00,
        _ => 0.0
    };

    /// <summary>只有 L3 以上的結果能更新 Agent 權重。L1/L2 只記錄不學習：否則評審被討好會汙染權重。</summary>
    public static bool UpdatesWeights(VerifierLevel l) => l >= VerifierLevel.L3_ExecutableTest;

    /// <summary>Triage 的「有無 verifier」以 L3 為界：L1/L2 只能檢查形式，不能檢核真假。</summary>
    public static bool CountsAsVerifier(VerifierLevel l) => l >= VerifierLevel.L3_ExecutableTest;
}
