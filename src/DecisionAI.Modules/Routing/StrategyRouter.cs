// ============================================================================
//  Routing 第三段：策略規則表（有序）。新策略 = 加一條 IStrategyRule + 一份 workflow JSON。
//  Rev2：策略必須知道自己有幾雙獨立的眼睛——IndependenceBudget 不足時禁用依賴獨立性的策略。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Modules.Agents;

namespace DecisionAI.Modules.Routing;

public sealed record StrategyChoice(Strategy Strategy, string WorkflowName, string Why);

public interface IStrategyRule
{
    StrategyChoice? TryApply(ProblemFacts facts, DegradationLevel degradation);
}

public sealed class PipelineRule : IStrategyRule
{
    public StrategyChoice? TryApply(ProblemFacts f, DegradationLevel d)
        => f.StageCount > 1 ? new(Strategy.MultiDomainPipeline, "multi_domain_pipeline", "多段跨領域 → 流水線 + 邊界閘") : null;
}

public sealed class VerifiedSolverRule : IStrategyRule
{
    public StrategyChoice? TryApply(ProblemFacts f, DegradationLevel d)
    {
        if (f.BestAvailableVerifier < VerifierLevel.L3_ExecutableTest) return null;

        // D10：只有一個模型時，禁用所有依賴獨立性的策略，退回單一 agent + 機器驗證
        if (d == DegradationLevel.SingleAgent)
            return new(Strategy.SingleAgent, "solver_verifier", "只有 1 個模型 → 禁用依賴獨立性的策略，退回單一 agent + 機器驗證");

        return f.Risk == RiskLevel.Low
            ? new(Strategy.SingleAgent, "solver_verifier", "有機器驗證器且低風險 → 單一 agent + 驗證就夠")
            : new(Strategy.SolverCriticVerifier, "engineering_root_cause", "有機器 / 實驗驗證器 → solver-critic-verifier");
    }
}

public interface IStrategyRouter
{
    WorkflowPlan Plan(ProblemFacts facts, PolicySnapshot policy, IndependenceBudget budget, DegradationLevel degradation);
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

    public WorkflowPlan Plan(ProblemFacts f, PolicySnapshot policy, IndependenceBudget budget, DegradationLevel degradation)
    {
        var choice = _rules.Select(r => r.TryApply(f, degradation)).FirstOrDefault(c => c is not null)
                     ?? throw new InvalidOperationException("沒有策略規則適用：Triage 應已攔下這個請求（fail-closed）");

        var why = ImmutableArray.CreateBuilder<string>();
        why.Add(choice.Why);

        int solverCount = choice.Strategy == Strategy.SingleAgent ? 1 : (f.Risk == RiskLevel.High ? 3 : 2);
        solverCount = Degradation.CapSolvers(degradation, solverCount);

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

        // ★ Rev2 §4.4 第一步：有 L3 機器驗證器時，L1 critic 的邊際價值極低——機器已經判過了。
        //   只有在「高風險」且「獨立性預算允許」時才值得再花一次批判。
        bool criticWorthIt = f.Risk == RiskLevel.High && budget.EnsembleClaimsPermitted;
        bool includeCritic = choice.Strategy == Strategy.SolverCriticVerifier && criticWorthIt;
        if (choice.Strategy == Strategy.SolverCriticVerifier)
            why.Add(includeCritic
                ? "高風險且獨立性充足 → 保留 L1 critic"
                : $"有 L3+ 機器驗證器{(budget.EnsembleClaimsPermitted ? "、風險非高" : "、獨立性不足")} → critic 為可選角色，本案略過（省一次模型呼叫）");

        string wfName = choice.Strategy == Strategy.SolverCriticVerifier && !includeCritic
            ? "engineering_root_cause_nocritic" : choice.WorkflowName;

        bool requireHuman = f.Risk != RiskLevel.Low || f.BestAvailableVerifier <= VerifierLevel.L2_Rule;
        return new WorkflowPlan(choice.Strategy, wfName, solverCount, includeCritic, requireHuman, degradation, why.ToImmutable());
    }
}
