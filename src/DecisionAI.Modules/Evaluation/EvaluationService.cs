// ============================================================================
//  Evaluation — 唯一能推進 PolicySnapshot 與 Catalog 的模組。只產生 Delta，不直接寫。
//  Rev2 新增回寫項目：ClaimCatalog 新條目候選（真假設未命中 catalog 時）。
//
//  Phase 3 把學習回路接完：Phase 2 定義了校準曲線、錯誤相關性、角色資格與 conformal
//  四組欄位，但沒有人把資料餵進去——欄位存在而永遠是空的，等於沒有學習。
//  這裡一次補齊四個來源：
//    · CalibrationPoints  每個 solver 對「每一條」假設的自評 vs 真假（不只 top-1，
//                         只記 top-1 的話校準曲線只會有一種機率值，畫不出曲線）
//    · PairObservations   兩兩 agent 是否同時錯 —— 這是「錯誤相關」的定義本身，
//                         也是 ProbeCalibratedEstimator 唯一的真實資料來源
//    · ConformalSample    當時的信念分布 + 事後真值，供離線重建分位數
//    · 實驗歷史頻率        在已知真假設的條件下，數出「這個實驗出哪個結果」的次數
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;

namespace DecisionAI.Modules.Evaluation;

public interface IEvaluationService
{
    EvaluationResult Ingest(Outcome outcome, CaseState s);
}

/// <summary>Delta 進 PolicySnapshot；Harvest 是跨案累積的原始樣本，由呼叫端寫進各自的 store。</summary>
public sealed record EvaluationResult(PolicyDelta Delta, EvaluationHarvest Harvest);

/// <summary>
/// Evaluation 的副產物：跨案累積的樣本。它們不屬於 PolicySnapshot（那是快照），
/// 而是重建分位數與歷史頻率的原始資料，因此分開回傳。
/// </summary>
public sealed record EvaluationHarvest(
    ImmutableArray<ConformalSample> ConformalSamples,
    ImmutableArray<ExperimentObservation> ExperimentObservations);

/// <summary>一次實驗在已知真假設條件下的觀察：這是似然「數出來」而不是「估出來」的來源。</summary>
public sealed record ExperimentObservation(string ExperimentKey, string ClaimCatalogId, int OutcomeIndex, int OutcomeCount);

public sealed class EvaluationService : IEvaluationService
{
    /// <summary>稽核集的抽樣比例。稽核集必須隨機抽，不能挑——挑過的樣本量不準覆蓋率。</summary>
    public double AuditFraction { get; init; } = 0.30;

    public EvaluationResult Ingest(Outcome o, CaseState s)
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

        // ── Phase 3 ①：校準點。每一條假設都記，不只首選 ──
        //    只記首選的話，這個 agent 的校準曲線上只會有「他最有信心的那一種機率」，
        //    等於用一個點去畫一條曲線。
        var calib = ImmutableArray.CreateBuilder<CalibrationPoint>();
        if (VerifierTrust.UpdatesWeights(o.Level))
            foreach (var claim in s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis))
            {
                bool truth = o.ClaimTruth.GetValueOrDefault(claim.LocalId, false);
                foreach (var p in claim.Proposals)
                    calib.Add(new CalibrationPoint(p.AgentId, p.Probability, truth));
            }

        // ── Phase 3 ②：兩兩錯誤相關。同時錯才是相關，一對一錯是獨立的證據 ──
        var pairs = ImmutableArray.CreateBuilder<PairObservation>();
        if (VerifierTrust.UpdatesWeights(o.Level))
        {
            var verdicts = TopPickByAgent(s, o);
            var agents = verdicts.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < agents.Count; i++)
                for (int j = i + 1; j < agents.Count; j++)
                {
                    bool a = verdicts[agents[i]], b = verdicts[agents[j]];
                    pairs.Add(new PairObservation(agents[i], agents[j], BothWrong: !a && !b, BothRight: a && b));
                }
            if (pairs.Count > 0)
                notes.Add($"錯誤相關觀察 {pairs.Count} 對：同時錯 {pairs.Count(p => p.BothWrong)}、" +
                          $"同時對 {pairs.Count(p => p.BothRight)}、一對一錯 {pairs.Count(p => !p.BothWrong && !p.BothRight)}");
        }

        // ── Phase 3 ③：conformal 樣本與實驗歷史頻率 ──
        var conformal = ImmutableArray.CreateBuilder<ConformalSample>();
        var expObs = ImmutableArray.CreateBuilder<ExperimentObservation>();
        if (VerifierTrust.UpdatesWeights(o.Level) && o.TrueClaimId is not null && s.Beliefs.Count > 0)
        {
            string family = TaskFamilyOf(s);
            // 稽核集以「case id 的雜湊」決定，不看樣本內容——這是可重現的隨機，不是挑選
            bool isAudit = StableFraction(s.Id) < AuditFraction;
            conformal.Add(new ConformalSample(family, s.Beliefs, o.TrueClaimId, isAudit));
            notes.Add($"conformal 樣本（{family}，{(isAudit ? "稽核集" : "校準集")}）：真值機率 {s.Beliefs.GetValueOrDefault(o.TrueClaimId, 0):P0}");

            string trueCatalogId = s.ClaimByLocalId(o.TrueClaimId)?.Key.Value ?? o.TrueClaimId;
            foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is int))
                expObs.Add(new ExperimentObservation(InMemoryExperimentCatalog.KeyOf(exp.Description),
                                                    trueCatalogId, exp.ObservedOutcome!.Value, exp.Outcomes.Length));
        }

        var delta = new PolicyDelta(s.Id, obs.ToImmutable(), cat.ToImmutable(), notes.ToImmutable())
        {
            CalibrationPoints = calib.ToImmutable(),
            PairObservations = pairs.ToImmutable()
        };
        return new EvaluationResult(delta, new EvaluationHarvest(conformal.ToImmutable(), expObs.ToImmutable()));
    }

    /// <summary>每個 solver 的首選對不對。錯誤相關看的是「結論」層級，不是逐條主張。</summary>
    private static Dictionary<string, bool> TopPickByAgent(CaseState s, Outcome o)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var agent in s.AgentsUsedAs("solver"))
        {
            var top = s.Claims.Where(k => k.Kind == ClaimKind.Hypothesis)
                .Select(k => (k, p: k.Proposals.FirstOrDefault(pr => pr.AgentId == agent)))
                .Where(x => x.p is not null)
                .OrderByDescending(x => x.p!.Probability).ThenBy(x => x.k.LocalId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (top.k is not null) result[agent] = o.ClaimTruth.GetValueOrDefault(top.k.LocalId, false);
        }
        return result;
    }

    private static string TaskFamilyOf(CaseState s) => TaskFamilyClassifier.Classify(s.Facts);

    /// <summary>以 case id 的穩定雜湊取 [0,1)：同一個 case 永遠落在同一邊，重放才會一致。</summary>
    internal static double StableFraction(string caseId)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in caseId) { h ^= c; h *= 16777619; }
            return (h % 10000) / 10000.0;
        }
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

/// <summary>
/// 跨案的 conformal 樣本庫（Phase 3）。校準集與稽核集分開存，
/// 分位數只能用校準集算，覆蓋率只能用稽核集量——混用會讓覆蓋率量不準。
/// </summary>
public interface IConformalSampleStore
{
    void Add(ConformalSample sample);
    IReadOnlyList<ConformalSample> For(string taskFamily);
    ImmutableDictionary<string, ConformalQuantile> RebuildAll(double alpha = 0.10);
    double EmpiricalCoverage(string taskFamily, ConformalQuantile q);
}

public sealed class InMemoryConformalSampleStore : IConformalSampleStore
{
    private readonly List<ConformalSample> _samples = new();
    private readonly object _lock = new();

    public void Add(ConformalSample s) { lock (_lock) _samples.Add(s); }

    public IReadOnlyList<ConformalSample> For(string family)
    {
        lock (_lock) return _samples.Where(s => s.TaskFamily == family).ToList();
    }

    public ImmutableDictionary<string, ConformalQuantile> RebuildAll(double alpha = 0.10)
    {
        List<ConformalSample> all;
        lock (_lock) all = _samples.ToList();
        return all.GroupBy(s => s.TaskFamily)
                  .Where(g => TaskFamilies.Whitelist.Contains(g.Key))     // 白名單外不建分位數，免得有人拿去用
                  .ToImmutableDictionary(g => g.Key, g => ConformalCalibrationBuilder.Build(g, alpha));
    }

    public double EmpiricalCoverage(string family, ConformalQuantile q)
        => ConformalCalibrationBuilder.EmpiricalCoverage(For(family), q);
}
