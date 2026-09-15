// ============================================================================
//  Evaluation — 唯一能推進 PolicySnapshot 的模組。只產生 PolicyDelta，不直接寫。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Evaluation;

public interface IEvaluationService
{
    PolicyDelta Ingest(Outcome outcome, CaseState s);
}

public sealed class EvaluationService : IEvaluationService
{
    public PolicyDelta Ingest(Outcome o, CaseState s)
    {
        var obs = ImmutableArray.CreateBuilder<WeightObservation>();
        var notes = ImmutableArray.CreateBuilder<string>();
        string domain = s.Domain;
        notes.Add($"真實結果（{o.Level}）：{o.Note}");

        // 1) Solver：他最有信心的假設是不是真的
        foreach (var agent in s.AgentsUsedAs("solver"))
        {
            var top = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis)
                .Select(k => (k, p: k.Proposals.FirstOrDefault(pr => pr.AgentId == agent)))
                .Where(x => x.p is not null).MaxBy(x => x.p!.StatedConfidence);
            if (top.k is null) continue;
            bool success = o.ClaimTruth.GetValueOrDefault(top.k.Id, false);
            obs.Add(new(agent, domain, "solver", success, o.Level));
            notes.Add($"{agent} 首選 {top.k.Id} → {(success ? "✓" : "✗")}");
        }

        // 2) Critic：把真假設打成高嚴重度 = 錯；把假假設打成低嚴重度 = 錯
        foreach (var v in s.Verifications.Where(v => v.Level == VerifierLevel.L1_LlmCritic && v.ClaimId is not null))
        {
            string agent = v.VerifierId.Split(':').Last();
            bool truth = o.ClaimTruth.GetValueOrDefault(v.ClaimId!, false);
            obs.Add(new(agent, domain, "critic", truth ? v.Pass == true : v.Pass == false, o.Level));
        }

        // 3) 實驗設計者：登記的似然表在觀察到的結果欄，是否把真假設排在最高
        if (o.TrueClaimId is not null)
            foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is int))
            {
                int idx = exp.ObservedOutcome!.Value;
                var best = exp.LikelihoodByClaim.Where(kv => idx < kv.Value.Length).MaxBy(kv => kv.Value[idx]);
                bool success = best.Key == o.TrueClaimId;
                obs.Add(new(exp.DesignedBy, domain, "experiment_designer", success, o.Level));
                notes.Add($"{exp.Id}（{exp.DesignedBy}）區分力 → {(success ? "✓" : "✗")}");
            }

        return new PolicyDelta(s.Id, obs.ToImmutable(), notes.ToImmutable());
    }
}

public sealed class InMemoryPolicyStore : IPolicyStore
{
    private readonly List<PolicySnapshot> _versions = new() { PolicySnapshot.Initial };
    private readonly object _lock = new();

    public PolicySnapshot Current { get { lock (_lock) return _versions[^1]; } }

    public PolicySnapshot Pin(long? version = null)
    {
        lock (_lock) return version is null ? _versions[^1] : _versions.First(v => v.Version == version);
    }

    public PolicySnapshot Commit(PolicyDelta delta)
    {
        lock (_lock)
        {
            var next = _versions[^1].Apply(delta);
            _versions.Add(next);
            return next;
        }
    }
}
