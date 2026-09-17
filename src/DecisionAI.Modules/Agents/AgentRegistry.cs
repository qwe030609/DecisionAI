// ============================================================================
//  Agents — Registry（讀 PolicySnapshot，本身無狀態）+ RoleAssigner + Runner
//  Rev2 Phase 1：帶約束的角色指派 + IndependenceBudget + 降級階梯。
//  （探針資格與 Thompson 抽樣在 Phase 2；這裡先用 Beta 均值並註明。）
// ============================================================================

using System.Collections.Immutable;
using System.Diagnostics;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Agents;

public sealed record RegisteredAgent(AgentSpec Spec, ILlm Model);

public sealed record SelectionQuery(string Role, string Domain, int Count, ImmutableHashSet<string>? Exclude = null);

public interface IAgentRegistry
{
    IReadOnlyList<RegisteredAgent> All { get; }
    RegisteredAgent Get(string id);
    IReadOnlyList<RegisteredAgent> Select(SelectionQuery q, PolicySnapshot policy);
    IReadOnlyList<RegisteredAgent> EligibleFor(string role, string domain);
}

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly List<RegisteredAgent> _agents = new();   // 註冊順序 = 決定性的 tie-break
    public double SameVendorPenalty { get; init; } = 0.05;
    public double SameFamilyPenalty { get; init; } = 0.10;

    public IReadOnlyList<RegisteredAgent> All => _agents;
    public RegisteredAgent Get(string id) => _agents.First(a => a.Spec.AgentId == id);

    public AgentRegistry Register(AgentSpec spec, ILlm model)
    {
        _agents.RemoveAll(a => a.Spec.AgentId == spec.AgentId);
        _agents.Add(new RegisteredAgent(spec, model));
        return this;
    }

    public IReadOnlyList<RegisteredAgent> EligibleFor(string role, string domain)
        => _agents.Where(a => a.Spec.EligibleRoles.Contains(role))
                  .Where(a => domain == "*" || a.Spec.Domains.Contains(domain) || a.Spec.Domains.Contains("*"))
                  .ToList();

    /// <summary>分數 = Beta 後驗均值 + 專精加分 − 同廠商 / 同家族懲罰。貪婪選 k 個。</summary>
    public IReadOnlyList<RegisteredAgent> Select(SelectionQuery q, PolicySnapshot policy)
    {
        var banned = q.Exclude ?? ImmutableHashSet<string>.Empty;
        var pool = EligibleFor(q.Role, q.Domain).Where(a => !banned.Contains(a.Spec.AgentId)).ToList();

        var chosen = new List<RegisteredAgent>();
        while (chosen.Count < q.Count && pool.Count > 0)
        {
            RegisteredAgent best = pool[0]; double bestScore = double.NegativeInfinity;
            foreach (var a in pool)
            {
                double s = policy.Weight(a.Spec.AgentId, q.Domain, q.Role).Mean
                         + (a.Spec.Domains.Contains(q.Domain) ? 0.02 : 0)
                         - SameVendorPenalty * chosen.Count(c => c.Spec.Vendor == a.Spec.Vendor)
                         - SameFamilyPenalty * chosen.Count(c => c.Spec.BaseModelFamily == a.Spec.BaseModelFamily);
                if (s > bestScore) { bestScore = s; best = a; }   // 嚴格大於 → 平手取先註冊者
            }
            chosen.Add(best); pool.Remove(best);
        }
        return chosen;
    }
}

// ── 角色指派：帶約束的指派問題 ────────────────────────────────────────

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
    AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy);
}

/// <summary>
/// 硬約束：critic ∉ 本案 solver；角色資格；規模小（個位數 × 個位數）所以貪婪即可。
/// 軟目標：角色間 family 多樣性最大化。
/// </summary>
public sealed class RoleAssigner : IRoleAssigner
{
    private readonly IAgentRegistry _registry;
    public RoleAssigner(IAgentRegistry registry) => _registry = registry;

    public IndependenceSurvey Survey(string domain)
    {
        var pool = _registry.All.Where(a => a.Spec.Domains.Contains(domain) || a.Spec.Domains.Contains("*")).ToList();
        int families = pool.Select(a => a.Spec.BaseModelFamily).Distinct().Count();
        int vendors  = pool.Select(a => a.Spec.Vendor).Distinct().Count();
        var budget = IndependenceBudget.Compute(families, vendors, pool.Count);
        var level  = IndependenceBudget.LevelFor(families, pool.Count);
        return new IndependenceSurvey(budget, level, ImmutableArray.Create(
            $"可用池：{budget.Describe()}", $"降級判定 {level}：{Degradation.Explain(level)}"));
    }

    public AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy)
    {
        var survey = Survey(demand.Domain);
        var (budget, level) = (survey.Budget, survey.Degradation);
        var why = survey.Rationale.ToBuilder();

        var slots = ImmutableArray.CreateBuilder<RoleSlot>();
        var usedAsSolver = new HashSet<string>();

        foreach (var (role, requested) in demand.Needs)
        {
            int count = role == "solver" ? Degradation.CapSolvers(level, requested) : requested;
            if (count < requested) why.Add($"{role}：{requested} → {count}（獨立性不足，多跑只是把成本乘以 N）");

            // 硬約束：critic 不得是本案 solver
            var exclude = role == "critic" ? usedAsSolver.ToImmutableHashSet() : ImmutableHashSet<string>.Empty;
            var picked = _registry.Select(new SelectionQuery(role, demand.Domain, count, exclude), policy);

            if (picked.Count < count)
                why.Add($"{role}：只找到 {picked.Count}/{count} 個合格 agent" + (role == "critic" ? "（排除本案 solver 後）" : ""));

            foreach (var a in picked)
            {
                slots.Add(new RoleSlot(role, a.Spec.AgentId, a.Spec.BaseModelFamily, a.Spec.Vendor));
                if (role == "solver") usedAsSolver.Add(a.Spec.AgentId);
            }
        }

        return new AssignmentResult(slots.ToImmutable(), budget, level, why.ToImmutable());
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

    public AssignmentResult Assign(RoleDemand demand, PolicySnapshot policy)
    {
        var r = _inner.Assign(demand, policy);
        return r with
        {
            Degradation = DegradationLevel.None,
            Budget = r.Budget with { EnsembleClaimsPermitted = true },
            Rationale = r.Rationale.Add("（壞的守門件）忽略降級階梯")
        };
    }
}

/// <summary>執行一個 agent：計時、估成本（4 chars/token）。不寫 Journal，回傳 AgentRun 由呼叫端記錄。</summary>
public sealed class AgentRunner
{
    public async Task<AgentRun> RunAsync(RegisteredAgent agent, LlmCallKey key, string role, string system, string user,
                                         double temperature, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string raw = await agent.Model.CompleteAsync(new LlmRequest(key, role, system, user, temperature), ct);
            double cost = (system.Length + user.Length) / 4.0 / 1000 * agent.Spec.Cost.InPer1k
                        + raw.Length / 4.0 / 1000 * agent.Spec.Cost.OutPer1k;
            return new AgentRun(agent.Spec.AgentId, role, raw, cost, sw.Elapsed, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentRun(agent.Spec.AgentId, role, "", 0, sw.Elapsed, false, ex.Message);
        }
    }
}
