// ============================================================================
//  Assurance — 從 CaseState 組出三層 + 拒絕判定。此資料夾不得出現 ILlm。
//
//  Phase 2：三層真的被填滿了——
//   · 覆蓋層：由 CoveragePredicted 事件帶進來（白名單外的題型根本不會有這個事件）
//   · 穩定層：三種擾動（機率 ±δ、假設集重抽樣、似然等級 ±1）永遠分開列，不合成一個數字
//   · 能力層：Chao1 覆蓋率太低時額外降級——沒把假設空間找完，驗證得再漂亮也只是在驗一個小角落
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

    /// <summary>Chao1 覆蓋率低於此值就降級能力層。預先登記，不在看到結果後調整。</summary>
    public double MinHypothesisCoverage { get; init; } = 0.60;

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

        // 假設覆蓋率：discovered 是本案已進機率引擎的假設數
        int discovered = s.Hypotheses.Count();
        if (s.Saturation is { } sat)
        {
            double coverage = Chao1SaturationEstimator.Coverage(discovered, sat);
            if (coverage < MinHypothesisCoverage)
                reasons.Add($"假設空間探索不足（Chao1 覆蓋率 {coverage:P0} < {MinHypothesisCoverage:P0}，singletons {sat.Singletons}）：" +
                            "驗證得再漂亮也只是在驗一個小角落");
        }
        if (s.DiversityCollapsed == true) reasons.Add("多樣性不足（處置階梯已用盡）：候選之間的差異只是措辭");

        var capability = new CapabilityLayer(level, VerifierTrust.Cap(level),
                                             reasons.Count > 0, reasons.ToImmutableArray());

        // 三擾動永遠分開列：合成一個數字就看不出是哪一種擾動讓結論翻掉
        var stability = s.Decision is { } d
            ? new StabilityLayer(d.Robustness, d.WorstCase, d.MaxRegret,
                                 DecisionStabilityUnderResampling: s.ResamplingStability,
                                 PosteriorOrderStableUnderLikertShift: s.SensitivityDetail is null ? null : !s.LikelihoodSensitive)
            : null;

        return new AssuranceReport(capability, s.Coverage, stability, abstention,
                                   LikelihoodSensitive: s.LikelihoodSensitive, HypothesisCoverage: s.Saturation);
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
