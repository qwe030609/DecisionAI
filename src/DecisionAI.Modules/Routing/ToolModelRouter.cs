// ============================================================================
//  Routing 第二段：Tool/Model Router（Rev2 §4.2，Phase 1 提前）
//  屬專業數值模型領域 → 委派外部工具，LLM 只負責翻譯與解釋，絕不自己編數值。
//  判準是「請求登記了 SpecialistDomain」這個事實，不是 LLM 的自我評估。
// ============================================================================

using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Routing;

public sealed record DelegationDecision(bool Delegate, SpecialistDomain? Specialist, string Reason);

public interface IToolModelRouter
{
    DelegationDecision Decide(DecisionRequest req);
}

public sealed class ToolModelRouter : IToolModelRouter
{
    private readonly IReadOnlySet<string> _availableTools;

    /// <summary>只有實際掛得上的工具才會被轉介；工具不在現場等於沒有這條路。</summary>
    public ToolModelRouter(IEnumerable<string>? availableTools = null)
        => _availableTools = (availableTools ?? new[] { "weibull_reliability", "cox_survival", "nwp_weather", "or_tools_tsp", "spice" }).ToHashSet();

    public DelegationDecision Decide(DecisionRequest req)
    {
        if (req.Specialist is not { } s)
            return new DelegationDecision(false, null, "未登記專業數值模型領域");
        if (!_availableTools.Contains(s.ToolId))
            return new DelegationDecision(false, null, $"登記了 {s.ToolId} 但現場沒有這個工具 → 不轉介");
        return new DelegationDecision(true, s, $"屬 {s.ToolId} 的領域：{s.WhyThisTool}");
    }

    public static DelegationHandoff Handoff(SpecialistDomain s)
        => new(s.ToolId, s.WhyThisTool, s.HowToRead, s.UncertaintyNotes, s.InputsRequired);
}

/// <summary>Mutation switch：從不轉介 → 系統會自己編專業領域的數值。</summary>
public sealed class NeverDelegateRouter : IToolModelRouter
{
    public DelegationDecision Decide(DecisionRequest req) => new(false, null, "（壞的守門件）一律不轉介");
}
