// ============================================================================
//  Routing 第一段：Verifiability Triage。
//  在花任何 LLM token 之前決定「該不該答」。判定拒絕 → 直接到 Abstention Gate。
//  Rev2：Triage 只給 ReasonCode 與說明；「該附什麼替代交付」由 Assurance 依型別決定。
// ============================================================================

using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Routing;

public sealed record TriageResult(bool Proceed, ReasonCode Code, string Explanation, ProblemFacts Facts);

public interface IVerifiabilityTriage
{
    TriageResult Assess(DecisionRequest req, VerifierInventory inventory);
}

public sealed class VerifiabilityTriage : IVerifiabilityTriage
{
    public TriageResult Assess(DecisionRequest req, VerifierInventory inv)
    {
        string domain = string.IsNullOrWhiteSpace(req.Domain) ? "general" : req.Domain;
        var facts = new ProblemFacts(inv.Best, req.Reflexive, req.Probabilistic, req.ActionsOrEmpty.Length > 0,
                                     Math.Max(1, req.StagesOrEmpty.Length), domain, req.Constraints.RiskLevel, req.GroundTruth);

        // 第三問先於第二問：定義爭議是比「沒有 ground truth」更具體的診斷
        if (req.GroundTruth == GroundTruthStatus.Contested)
            return new(false, ReasonCode.DefinitionalDispute,
                "爭議在「答案的定義」本身，任何 verifier 都無法檢核；系統不裁決定義。", facts);

        if (req.GroundTruth == GroundTruthStatus.Undefined)
            return new(false, ReasonCode.NoGroundTruth,
                "沒有各方同意的 ground truth（價值觀 / 品味問題），無法給出可檢核的答案。", facts);

        if (req.Reflexive)
            return new(false, ReasonCode.ReflexiveDirection,
                "反身系統：預測本身會改變系統行為，方向預測不可驗證。", facts);

        if (!inv.HasRealVerifier)
            return new(false, ReasonCode.NoVerifier,
                $"沒有 L3 以上的 verifier（目前最高 {inv.Best}）；只有 LLM 互評與形式規則，無法檢核真假。", facts);

        return new(true, ReasonCode.None, "有可執行 / 可實驗的 verifier，且 ground truth 明確。", facts);
    }
}

/// <summary>Mutation switch：永遠放行 —— 等同一個沒有拒答通道的 router。</summary>
public sealed class AlwaysProceedTriage : IVerifiabilityTriage
{
    public TriageResult Assess(DecisionRequest req, VerifierInventory inv)
    {
        string domain = string.IsNullOrWhiteSpace(req.Domain) ? "general" : req.Domain;
        return new(true, ReasonCode.None, "（壞的守門件）一律放行",
            new ProblemFacts(inv.Best, req.Reflexive, req.Probabilistic, req.ActionsOrEmpty.Length > 0,
                             Math.Max(1, req.StagesOrEmpty.Length), domain, req.Constraints.RiskLevel, req.GroundTruth));
    }
}
