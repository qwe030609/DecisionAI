// ============================================================================
//  Evaluation — 唯一能推進 PolicySnapshot 與 Catalog 的模組。只產生 Delta，不直接寫。
//  Rev2 新增回寫項目：ClaimCatalog 新條目候選（真假設未命中 catalog 時）。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Catalog;

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
        var cat = ImmutableArray.CreateBuilder<CatalogEntryCandidate>();
        var notes = ImmutableArray.CreateBuilder<string>();
        string domain = s.Domain;
        notes.Add($"真實結果（{o.Level}）：{o.Note}");

        // 1) Solver：他最有信心的假設是不是真的
        foreach (var agent in s.AgentsUsedAs("solver"))
        {
            var top = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis)
                .Select(k => (k, p: k.Proposals.FirstOrDefault(pr => pr.AgentId == agent)))
                .Where(x => x.p is not null)
                .OrderByDescending(x => x.p!.Probability).ThenBy(x => x.k.LocalId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (top.k is null) continue;
            bool success = o.ClaimTruth.GetValueOrDefault(top.k.LocalId, false);
            obs.Add(new(agent, domain, "solver", success, o.Level));
            notes.Add($"{agent} 首選 {top.k.LocalId} → {(success ? "✓" : "✗")}");
        }

        // 2) Critic：把真假設打成高嚴重度 = 錯；把假假設打成低嚴重度 = 錯
        foreach (var v in s.Verifications.Where(v => v.Level == VerifierLevel.L1_LlmCritic && v.ClaimId is not null))
        {
            string agent = v.VerifierId.Split(':').Last();
            bool truth = o.ClaimTruth.GetValueOrDefault(v.ClaimId!, false);
            obs.Add(new(agent, domain, "critic", truth ? v.Pass == true : v.Pass == false, o.Level));
        }

        // 3) 實驗提名者：登記的似然表在觀察到的結果欄，是否把真假設排在最高
        if (o.TrueClaimId is not null)
            foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is int))
            {
                int idx = exp.ObservedOutcome!.Value;
                var best = exp.LikelihoodByClaim.Where(kv => idx < kv.Value.Length)
                    .OrderByDescending(kv => kv.Value[idx]).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                bool success = best.Key == o.TrueClaimId;
                obs.Add(new(exp.NominatedBy, domain, "experiment_designer", success, o.Level));
                notes.Add($"{exp.Id}（提名者 {exp.NominatedBy}）區分力 → {(success ? "✓" : "✗")}");
            }

        // 4) ★ Catalog 回寫：真假設若沒對映到既有 catalog entry，提名成新條目（待 arbiter 核准）
        if (VerifierTrust.UpdatesWeights(o.Level) && o.TrueClaimId is not null)
        {
            var trueClaim = s.ClaimByLocalId(o.TrueClaimId);
            if (trueClaim is not null)
            {
                bool hit = trueClaim.Key.IsCatalogued;
                notes.Add($"Catalog {(hit ? "命中" : "未命中")}：{o.TrueClaimId} → {trueClaim.Key}（{trueClaim.Match}）");
                cat.Add(new CatalogEntryCandidate(trueClaim.Key.Value, trueClaim.Frame, domain, ConfirmedTrue: true));
            }
        }

        return new PolicyDelta(s.Id, obs.ToImmutable(), cat.ToImmutable(), notes.ToImmutable());
    }
}

/// <summary>把 PolicyDelta 的 Catalog 候選寫進 Catalog。執行中的 case 不受影響（讀的是 pin 住的版本）。</summary>
public sealed class CatalogWriter
{
    private readonly IClaimCatalog _catalog;
    public CatalogWriter(IClaimCatalog catalog) => _catalog = catalog;

    public IReadOnlyList<string> Commit(PolicyDelta delta)
    {
        var written = new List<string>();
        foreach (var c in delta.CatalogEntries)
        {
            var entry = _catalog.Register(c.Frame, c.Domain, c.ConfirmedTrue);
            written.Add(entry.CatalogId);
        }
        return written;
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
