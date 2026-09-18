// ============================================================================
//  角色資格探針（Rev2 §4.3 / Phase 2）
//  用量測取代猜測。最關鍵的是 critic：它的能力就是「抓得到植入的錯」，
//  這件事可以精確量測，而且不需要等真實 outcome——離線批跑即可。
//  這正面解決了「critic 表現無法評估」這個老問題。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Agents;

public sealed record ProbeScore(double Score, bool Passed, string Detail);

public interface IRoleQualificationProbe
{
    string Role { get; }
    string Id { get; }
    Task<ProbeScore> RunAsync(RegisteredAgent agent, CancellationToken ct = default);
}

/// <summary>solver 探針：已知根因的診斷題，看真假設有沒有進入前 k 名。</summary>
public sealed class SolverProbe : IRoleQualificationProbe
{
    private readonly ImmutableArray<(string Prompt, string TrueMechanism, string TrueLocus)> _items;
    public string Role => "solver";
    public string Id => "solver-probe";
    public int TopK { get; init; } = 2;
    public double PassThreshold { get; init; } = 0.5;

    public SolverProbe(params (string Prompt, string TrueMechanism, string TrueLocus)[] items)
        => _items = items.ToImmutableArray();

    public async Task<ProbeScore> RunAsync(RegisteredAgent agent, CancellationToken ct = default)
    {
        int hit = 0;
        foreach (var (prompt, mech, locus) in _items)
        {
            var req = new LlmRequest(new LlmCallKey("PROBE", Id, agent.Spec.AgentId, hit), "solver",
                                     RolePrompts.Solver, prompt, 0.2);
            var hyps = AgentOutputs.Hypotheses(await agent.Model.CompleteAsync(req, ct));
            if (hyps.Take(TopK).Any(h => h.Frame.Mechanism == mech && ClaimFrame.Norm(h.Frame.Locus) == ClaimFrame.Norm(locus))) hit++;
        }
        double score = _items.Length == 0 ? 0 : hit / (double)_items.Length;
        return new ProbeScore(score, score >= PassThreshold, $"{hit}/{_items.Length} 題的真根因進入前 {TopK} 名");
    }
}

/// <summary>
/// critic 探針：一組主張裡植入已知的錯，量偵測率（precision / recall）。
/// 這是整個 Phase 2 最實用的一條——critic 不必等真實 outcome 就能被評分。
/// </summary>
public sealed class CriticProbe : IRoleQualificationProbe
{
    private readonly string _claimBlock;
    private readonly ImmutableHashSet<string> _plantedBadClaims;
    public string Role => "critic";
    public string Id => "critic-probe";
    public double PassThreshold { get; init; } = 0.6;

    public CriticProbe(string claimBlock, params string[] plantedBadClaims)
    { _claimBlock = claimBlock; _plantedBadClaims = plantedBadClaims.ToImmutableHashSet(); }

    public async Task<ProbeScore> RunAsync(RegisteredAgent agent, CancellationToken ct = default)
    {
        var req = new LlmRequest(new LlmCallKey("PROBE", Id, agent.Spec.AgentId, 0), "critic",
                                 RolePrompts.Critic, _claimBlock, 0.2);
        var critiques = AgentOutputs.Critiques(await agent.Model.CompleteAsync(req, ct));

        // 「抓到」= 對植入錯誤的主張給出高嚴重度
        var flagged = critiques.Where(c => c.Severity >= 0.5).Select(c => c.ClaimId).ToImmutableHashSet();
        int tp = flagged.Intersect(_plantedBadClaims).Count;
        int fp = flagged.Except(_plantedBadClaims).Count;
        int fn = _plantedBadClaims.Except(flagged).Count;

        double precision = tp + fp == 0 ? 0 : tp / (double)(tp + fp);
        double recall = tp + fn == 0 ? 0 : tp / (double)(tp + fn);
        double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new ProbeScore(f1, f1 >= PassThreshold,
            $"植入 {_plantedBadClaims.Count} 個錯，抓到 {tp}、誤報 {fp}、漏抓 {fn} → precision {precision:F2}、recall {recall:F2}、F1 {f1:F2}");
    }
}

/// <summary>designer 探針：提名的實驗能不能把真假設的似然拉開（區分力）。</summary>
public sealed class DesignerProbe : IRoleQualificationProbe
{
    private readonly string _prompt;
    private readonly string _trueClaimId;
    public string Role => "experiment_designer";
    public string Id => "designer-probe";
    public double PassThreshold { get; init; } = 0.5;

    public DesignerProbe(string prompt, string trueClaimId) { _prompt = prompt; _trueClaimId = trueClaimId; }

    public async Task<ProbeScore> RunAsync(RegisteredAgent agent, CancellationToken ct = default)
    {
        var req = new LlmRequest(new LlmCallKey("PROBE", Id, agent.Spec.AgentId, 0), "experiment_designer",
                                 RolePrompts.ExperimentDesigner, _prompt, 0.2);
        var exps = AgentOutputs.Experiments(await agent.Model.CompleteAsync(req, ct));
        if (exps.Count == 0) return new ProbeScore(0, false, "沒有提名任何實驗");

        // 區分力：真假設在某一結果欄的等級，與其他假設在同欄的最高等級差幾級
        double best = 0;
        foreach (var e in exps)
        {
            var mine = e.Votes.FirstOrDefault(v => v.ClaimLocalId == _trueClaimId);
            if (mine is null) continue;
            for (int o = 0; o < mine.ByOutcome.Length; o++)
            {
                int others = e.Votes.Where(v => v.ClaimLocalId != _trueClaimId && o < v.ByOutcome.Length)
                                    .Select(v => (int)v.ByOutcome[o]).DefaultIfEmpty(2).Max();
                best = Math.Max(best, ((int)mine.ByOutcome[o] - others) / 4.0);
            }
        }
        double score = Math.Clamp(best, 0, 1);
        return new ProbeScore(score, score >= PassThreshold, $"真假設與其他假設的最大等級差 {best * 4:F0} 級 → 區分力 {score:F2}");
    }
}

/// <summary>離線批跑所有探針，產出可寫回 PolicySnapshot 的結果。</summary>
public sealed class ProbeRunner
{
    private readonly IReadOnlyList<IRoleQualificationProbe> _probes;
    public ProbeRunner(params IRoleQualificationProbe[] probes) => _probes = probes;

    public async Task<ImmutableArray<ProbeResult>> RunAllAsync(IEnumerable<RegisteredAgent> agents, CancellationToken ct = default)
    {
        var results = ImmutableArray.CreateBuilder<ProbeResult>();
        foreach (var probe in _probes)
            foreach (var agent in agents.Where(a => a.Spec.EligibleRoles.Contains(probe.Role))
                                        .OrderBy(a => a.Spec.AgentId, StringComparer.Ordinal))
            {
                var s = await probe.RunAsync(agent, ct);
                results.Add(new ProbeResult(agent.Spec.AgentId, probe.Role, s.Passed, s.Score));
            }
        return results.ToImmutable();
    }
}
