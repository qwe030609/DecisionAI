// ============================================================================
//  Routing 第三段：策略規則表（有序）。新策略 = 加一條 IStrategyRule + 一份 workflow JSON。
//  Phase 1 只有兩條規則：多段流水線、有機器驗證器的 solver-critic-verifier。
//  （第二段 Tool/Model Router 在 Phase 2。）
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Agents;

namespace DecisionAI.Modules.Routing;

public sealed record StrategyChoice(Strategy Strategy, string WorkflowName, string Why);

public interface IStrategyRule
{
    StrategyChoice? TryApply(ProblemFacts facts);
}

public sealed class PipelineRule : IStrategyRule
{
    public StrategyChoice? TryApply(ProblemFacts f)
        => f.StageCount > 1 ? new(Strategy.MultiDomainPipeline, "multi_domain_pipeline", "多段跨領域 → 流水線 + 邊界閘") : null;
}

public sealed class VerifiedSolverRule : IStrategyRule
{
    public StrategyChoice? TryApply(ProblemFacts f)
    {
        if (f.BestAvailableVerifier < VerifierLevel.L3_ExecutableTest) return null;
        return f.Risk == RiskLevel.Low
            ? new(Strategy.SingleAgent, "solver_verifier", "有機器驗證器且低風險 → 單一 agent + 驗證就夠")
            : new(Strategy.SolverCriticVerifier, "engineering_root_cause", "有機器 / 實驗驗證器 → solver-critic-verifier");
    }
}

public interface IStrategyRouter
{
    WorkflowPlan Plan(ProblemFacts facts, PolicySnapshot policy);
}

public sealed class StrategyRouter : IStrategyRouter
{
    private readonly IReadOnlyList<IStrategyRule> _rules;
    private readonly IAgentRegistry _registry;

    public StrategyRouter(IAgentRegistry registry, IReadOnlyList<IStrategyRule>? rules = null)
    {
        _registry = registry;
        _rules = rules ?? new IStrategyRule[] { new PipelineRule(), new VerifiedSolverRule() };
    }

    public WorkflowPlan Plan(ProblemFacts f, PolicySnapshot policy)
    {
        var choice = _rules.Select(r => r.TryApply(f)).FirstOrDefault(c => c is not null)
                     ?? throw new InvalidOperationException("沒有策略規則適用：Triage 應已攔下這個請求（fail-closed）");

        var why = ImmutableArray.CreateBuilder<string>();
        why.Add(choice.Why);

        int solverCount = choice.Strategy == Strategy.SingleAgent ? 1 : (f.Risk == RiskLevel.High ? 3 : 2);
        if (solverCount > 1)
        {
            var top = _registry.Select(new SelectionQuery("solver", f.Domain, 1), policy).FirstOrDefault();
            if (top is not null)
            {
                var w = policy.Weight(top.Spec.AgentId, f.Domain, "solver");
                if (w.N >= 5 && w.Mean >= 0.85) { solverCount--; why.Add($"歷史（policy v{policy.Version}）：{top.Spec.AgentId} 在 {f.Domain} 上 {w.Mean:P0}（n={w.N}）→ 少用一個 solver"); }
                else why.Add($"歷史資料不足（最佳 solver n={w.N}）→ 維持 {solverCount} 個 solver");
            }
        }

        bool requireHuman = f.Risk != RiskLevel.Low || f.BestAvailableVerifier <= VerifierLevel.L2_Rule;
        return new WorkflowPlan(choice.Strategy, choice.WorkflowName, solverCount, requireHuman, why.ToImmutable());
    }
}
