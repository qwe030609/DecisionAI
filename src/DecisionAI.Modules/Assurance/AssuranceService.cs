// ============================================================================
//  Assurance — 從 CaseState 組出三層 + 拒絕判定。此資料夾不得出現 ILlm。
//  Phase 1：能力層 + Abstention；覆蓋層留 null。
// ============================================================================

using DecisionAI.Core.Assurance;
using DecisionAI.Core.Journal;

namespace DecisionAI.Modules.Assurance;

public interface IAbstentionGate
{
    AbstentionLayer Evaluate(CaseState s);
}

public sealed class AbstentionGate : IAbstentionGate
{
    public AbstentionLayer Evaluate(CaseState s)
    {
        // 1) Triage / 先前的明確拒答
        if (s.Abstention is { Abstained: true } a) return a;

        // 2) 邊界攔截 / 人類拒絕 / 引擎中止 → 沒有完整結果可背書
        if (s.Halted)
            return new AbstentionLayer(true, ReasonCode.PipelineHalted, $"流程中止：{s.HaltReason}",
                s.SkippedSteps.Length > 0 ? $"未執行：{string.Join(", ", s.SkippedSteps)}；修正後可重新提交" : null);

        // 3) 有效用矩陣但沒有任何可接受行動 → 交人
        if (s.Utilities.Length > 0 && s.Decision is { RecommendedAction: null } d)
            return new AbstentionLayer(true, ReasonCode.NoAdmissibleAction, d.Reason, "由人決定是否放寬可接受損失或增加行動選項");

        return AbstentionLayer.NotAbstained;
    }
}

public interface IAssuranceService
{
    AssuranceReport Build(CaseState s);
}

public sealed class AssuranceService : IAssuranceService
{
    private readonly IAbstentionGate _gate;
    public AssuranceService(IAbstentionGate gate) => _gate = gate;

    public AssuranceReport Build(CaseState s)
    {
        var abstention = _gate.Evaluate(s);
        if (abstention.Abstained)
            return new AssuranceReport(null, null, null, abstention);     // 拒答時不輸出任何信心相關數值

        var level = s.BestPassedLevel();
        var capability = new CapabilityLayer(level, Core.Domain.VerifierTrust.Cap(level), Degraded: s.PartialSteps.Count > 0);
        var stability = s.Decision is { } d ? new StabilityLayer(d.Robustness, d.WorstCase, d.MaxRegret) : null;
        return new AssuranceReport(capability, Coverage: null, stability, abstention);
    }
}
