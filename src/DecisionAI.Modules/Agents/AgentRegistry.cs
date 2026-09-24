// ============================================================================
//  Agents — Registry（讀 PolicySnapshot，本身無狀態）+ RoleAssigner + Runner
//  Registry 本身無狀態：權重來自 case pin 住的 PolicySnapshot。
//  角色指派、探針資格與 Thompson 抽樣在 RoleAssigner.cs / Probes.cs。
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
