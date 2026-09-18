// ============================================================================
//  角色指派（Rev2 §4.3 / Phase 2）：帶約束的指派問題，不是逐一挑最佳。
//
//  Thompson 抽樣取代 Beta 均值：從後驗抽樣而非取均值。改動極小，性質完全不同——
//  冷啟動時等同隨機（這是正確行為，因為此時確實不知道誰行），
//  不確定性大的 arm 自然被多探索，證據累積後自動收斂到 exploit。
//
//  洗牌政策：探針後驗區間不重疊（顯著較優）→ 不洗；差異不顯著 → 洗；
//  任何情況固定 10% 強制輪換。第二條常被忽略：固定角色會把個別模型的盲點
//  升格為系統性盲點——那是跨時間的錯誤相關。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Agents;

public sealed record AssignmentResult(
    ImmutableArray<RoleSlot> Slots,
    IndependenceBudget Budget,
    DegradationLevel Degradation,
    ImmutableArray<string> Rationale)
{
    public IEnumerable<string> AgentsFor(string role) => Slots.Where(s => s.Role == role).Select(s => s.AgentId);
}

/// <summary>先清點可用的獨立性，策略才知道自己有幾雙獨立的眼睛（Rev2 §5 的 ④ 提前到 ⑤ 之前）。</summary>
public sealed record IndependenceSurvey(IndependenceBudget Budget, DegradationLevel Degradation, ImmutableArray<string> Rationale);

public interface IRoleAssigner
{
    IndependenceSurvey Survey(string domain);
    AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy, IRandomSource rng);
}

public sealed class RoleAssigner : IRoleAssigner
{
    private readonly IAgentRegistry _registry;
    public RoleAssigner(IAgentRegistry registry) => _registry = registry;

    /// <summary>探針跑過且明顯不合格 → 直接排除。沒跑過探針的不擋（冷啟動時大家都沒跑過）。</summary>
    public double MinRoleFit { get; init; } = 0.35;
    public int MinProbeRuns { get; init; } = 2;
    /// <summary>starvation 防護：與「稽核集必須隨機」同一邏輯。</summary>
    public double ForcedRotation { get; init; } = 0.10;
    public double SameVendorPenalty { get; init; } = 0.05;
    public double SameFamilyPenalty { get; init; } = 0.10;

    public IndependenceSurvey Survey(string domain)
    {
        var pool = _registry.All.Where(a => a.Spec.Domains.Contains(domain) || a.Spec.Domains.Contains("*")).ToList();
        int families = pool.Select(a => a.Spec.BaseModelFamily).Distinct().Count();
        int vendors = pool.Select(a => a.Spec.Vendor).Distinct().Count();
        var budget = IndependenceBudget.Compute(families, vendors, pool.Count);
        var level = IndependenceBudget.LevelFor(families, pool.Count);
        return new IndependenceSurvey(budget, level, ImmutableArray.Create(
            $"可用池：{budget.Describe()}", $"降級判定 {level}：{Degradation.Explain(level)}"));
    }

    public AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy, IRandomSource rng)
    {
        var survey = Survey(demand.Domain);
        var (budget, level) = (survey.Budget, survey.Degradation);
        var why = survey.Rationale.ToBuilder();

        var slots = ImmutableArray.CreateBuilder<RoleSlot>();
        var usedAsSolver = new HashSet<string>();
        var usedAsAnalyst = new HashSet<string>();
        var stream = rng.Fork("role_selection");

        foreach (var (role, requested) in demand.Needs)
        {
            int count = role == "solver" ? Degradation.CapSolvers(level, requested) : requested;
            if (count < requested) why.Add($"{role}：{requested} → {count}（獨立性不足，多跑只是把成本乘以 N）");

            // 硬約束：critic ∉ 本案 solver；synthesizer ∉ 本案 analyst
            var banned = role switch
            {
                "critic" => usedAsSolver.ToImmutableHashSet(),
                "synthesizer" => usedAsAnalyst.ToImmutableHashSet(),
                _ => ImmutableHashSet<string>.Empty
            };

            var pool = _registry.EligibleFor(role, demand.Domain).Where(a => !banned.Contains(a.Spec.AgentId)).ToList();

            // 角色資格門檻：探針跑過且明顯不合格的排除
            var unfit = pool.Where(a =>
            {
                var f = policy.Fit(a.Spec.AgentId, role);
                return f.ProbeRuns >= MinProbeRuns && f.Mean < MinRoleFit;
            }).ToList();
            if (unfit.Count > 0)
            {
                why.Add($"{role}：探針判定不合格而排除 {string.Join("、", unfit.Select(a => $"{a.Spec.AgentId}({policy.Fit(a.Spec.AgentId, role).Mean:F2})"))}");
                pool = pool.Except(unfit).ToList();
            }

            if (pool.Count == 0) { why.Add($"{role}：排除後沒有合格的 agent"); continue; }

            var picked = PickWithThompson(pool, role, demand.Domain, count, policy, stream, why);

            foreach (var a in picked)
            {
                slots.Add(new RoleSlot(role, a.Spec.AgentId, a.Spec.BaseModelFamily, a.Spec.Vendor));
                if (role == "solver") usedAsSolver.Add(a.Spec.AgentId);
                if (role == "analyst") usedAsAnalyst.Add(a.Spec.AgentId);
            }
        }

        return new AssignmentResult(slots.ToImmutable(), budget, level, why.ToImmutable());
    }

    private List<RegisteredAgent> PickWithThompson(List<RegisteredAgent> pool, string role, string domain, int count,
                                                   PolicySnapshot policy, IRandomSource rng, ImmutableArray<string>.Builder why)
    {
        var chosen = new List<RegisteredAgent>();
        var remaining = new List<RegisteredAgent>(pool);

        // 洗牌判定：看排名前二的角色資格後驗是否重疊
        var ranked = pool.OrderByDescending(a => policy.Fit(a.Spec.AgentId, role).Mean)
                         .ThenBy(a => a.Spec.AgentId, StringComparer.Ordinal).ToList();
        bool separated = false;
        if (ranked.Count >= 2)
        {
            var (f1, f2) = (policy.Fit(ranked[0].Spec.AgentId, role).Posterior, policy.Fit(ranked[1].Spec.AgentId, role).Posterior);
            double lo1 = f1.Mean - 2 * Math.Sqrt(f1.Variance), hi2 = f2.Mean + 2 * Math.Sqrt(f2.Variance);
            separated = f1.N >= MinProbeRuns && f2.N >= MinProbeRuns && lo1 > hi2;
            why.Add(separated
                ? $"{role}：{ranked[0].Spec.AgentId} 的後驗區間與次位不重疊 → 不洗牌（洗牌是純損失）"
                : $"{role}：候選之間差異不顯著 → 洗牌，避免同一模型長期固定當 {role} 而讓它的盲點每案漏同一類錯");
        }

        while (chosen.Count < count && remaining.Count > 0)
        {
            var scored = remaining.Select(a =>
            {
                var post = policy.Weight(a.Spec.AgentId, domain, role);
                double s = separated
                    ? post.Mean
                    : post.Sample(rng.NextDouble(), rng.NextDouble());     // Thompson：從後驗抽樣
                s += a.Spec.Domains.Contains(domain) ? 0.02 : 0;
                s -= SameVendorPenalty * chosen.Count(c => c.Spec.Vendor == a.Spec.Vendor);
                s -= SameFamilyPenalty * chosen.Count(c => c.Spec.BaseModelFamily == a.Spec.BaseModelFamily);
                return (agent: a, score: s);
            }).OrderByDescending(x => x.score).ThenBy(x => x.agent.Spec.AgentId, StringComparer.Ordinal).ToList();

            // 固定強制輪換：即使有人長期領先，也要保留探索
            int idx = 0;
            if (!separated && scored.Count > 1 && rng.NextDouble() < ForcedRotation)
            {
                idx = 1 + rng.Next(scored.Count - 1);
                why.Add($"{role}：強制輪換（{ForcedRotation:P0}）→ 改用 {scored[idx].agent.Spec.AgentId}");
            }

            chosen.Add(scored[idx].agent);
            remaining.Remove(scored[idx].agent);
        }
        return chosen;
    }
}

/// <summary>Mutation switch：忽略降級階梯，永遠宣稱完整獨立性。</summary>
public sealed class IgnoreDegradationAssigner : IRoleAssigner
{
    private readonly IRoleAssigner _inner;
    public IgnoreDegradationAssigner(IRoleAssigner inner) => _inner = inner;

    public IndependenceSurvey Survey(string domain)
    {
        var s = _inner.Survey(domain);
        return s with { Degradation = DegradationLevel.None, Budget = s.Budget with { EnsembleClaimsPermitted = true } };
    }

    public AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy, IRandomSource rng)
    {
        var r = _inner.Assign(demand, policy, rng);
        return r with
        {
            Degradation = DegradationLevel.None,
            Budget = r.Budget with { EnsembleClaimsPermitted = true },
            Rationale = r.Rationale.Add("（壞的守門件）忽略降級階梯")
        };
    }
}

/// <summary>Mutation switch：Thompson 改回 argmax mean → 領先者永遠鎖死，後驗不再更新。</summary>
public sealed class ArgmaxMeanAssigner : IRoleAssigner
{
    private readonly IAgentRegistry _registry;
    private readonly RoleAssigner _inner;
    public ArgmaxMeanAssigner(IAgentRegistry registry) { _registry = registry; _inner = new RoleAssigner(registry); }

    public IndependenceSurvey Survey(string domain) => _inner.Survey(domain);

    public AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy, IRandomSource rng)
    {
        var survey = Survey(demand.Domain);
        var slots = ImmutableArray.CreateBuilder<RoleSlot>();
        var usedAsSolver = new HashSet<string>();
        foreach (var (role, requested) in demand.Needs)
        {
            int count = role == "solver" ? Degradation.CapSolvers(survey.Degradation, requested) : requested;
            var banned = role == "critic" ? usedAsSolver.ToImmutableHashSet() : ImmutableHashSet<string>.Empty;
            foreach (var a in _registry.Select(new SelectionQuery(role, demand.Domain, count, banned), policy))
            {
                slots.Add(new RoleSlot(role, a.Spec.AgentId, a.Spec.BaseModelFamily, a.Spec.Vendor));
                if (role == "solver") usedAsSolver.Add(a.Spec.AgentId);
            }
        }
        return new AssignmentResult(slots.ToImmutable(), survey.Budget, survey.Degradation,
            survey.Rationale.Add("（壞的守門件）用 argmax mean 取代 Thompson，沒有探索也沒有輪換"));
    }
}
