// ============================================================================
//  Assurance — 從 CaseState 組出三層 + 拒絕判定。此資料夾不得出現 ILlm。
//  Phase 1：能力層 + Abstention（含型別化 payload）；覆蓋層留 null，三擾動 Stability 在 Phase 2。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
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
        // 1) Triage / Tool-Model Router 的明確拒答（payload 已在事件流裡）
        if (s.Abstention is { Abstained: true } a) return a;

        // 2) 邊界攔截 / 人類拒絕 / 引擎中止 → 沒有完整結果可背書
        if (s.Halted)
            return new AbstentionLayer(true, ReasonCode.PipelineHalted, $"流程中止：{s.HaltReason}",
                DeterministicPayloads.Halted(s.HaltReason ?? "未知原因", s.SkippedSteps));

        // 3) 有效用矩陣但沒有任何可接受行動 → 交人
        if (s.Utilities.Length > 0 && s.Decision is { RecommendedAction: null } d)
            return new AbstentionLayer(true, ReasonCode.NoAdmissibleAction, d.Reason,
                DeterministicPayloads.NoAdmissibleAction(d.Reason));

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
        var reasons = new List<string>();
        if (s.PartialSteps.Count > 0) reasons.Add($"逾時降級的步驟：{string.Join(", ", s.PartialSteps.OrderBy(x => x))}");
        if (s.Degradation != DegradationLevel.None)
            reasons.Add($"獨立性不足（{s.Degradation}）：{Degradation.Explain(s.Degradation)}");
        if (s.Budget is { EnsembleClaimsPermitted: false })
            reasons.Add("不准宣稱集成增益：多個一致的答案在此不構成額外證據");

        var capability = new CapabilityLayer(level, VerifierTrust.Cap(level),
                                             reasons.Count > 0, reasons.ToImmutableArray());
        var stability = s.Decision is { } d ? new StabilityLayer(d.Robustness, d.WorstCase, d.MaxRegret) : null;
        return new AssuranceReport(capability, Coverage: null, stability, abstention,
                                   LikelihoodSensitive: false, HypothesisCoverage: s.Saturation);
    }
}

/// <summary>Mutation switch：能力層浮報 + 憑空宣稱覆蓋保證 + 永不拒答。</summary>
public sealed class OverclaimingAssurance : IAssuranceService
{
    public AssuranceReport Build(CaseState s) => new(
        new CapabilityLayer(VerifierLevel.L4_Experiment, 0.95, false, ImmutableArray<string>.Empty),
        new CoverageLayer(0.90, ImmutableArray.Create("（壞的守門件）憑空宣稱的預測集合")),
        s.Decision is { } d ? new StabilityLayer(d.Robustness, d.WorstCase, d.MaxRegret) : null,
        AbstentionLayer.NotAbstained);
}

/// <summary>Mutation switch：拒答閘永不拒答。</summary>
public sealed class NeverAbstainGate : IAbstentionGate
{
    public AbstentionLayer Evaluate(CaseState s) => AbstentionLayer.NotAbstained;
}
