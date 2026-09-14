// ============================================================================
//  Evaluation System — 決定系統會不會越用越聰明的地方
//  預測 → 行動 → 真實 Outcome → 誰對誰錯 → 校準 / 權重 / 相關性回饋。
//  只有 L3 以上的 outcome 會改權重（見 VerifierTrust.UpdatesWeights）。
// ============================================================================

using DecisionAI.Agents;
using DecisionAI.Domain;
using DecisionAI.Probability;

namespace DecisionAI.Evaluation;

public sealed class EvaluationSystem
{
    private readonly AgentRegistry _registry;
    private readonly CalibrationEngine _calibration;
    public EvaluationSystem(AgentRegistry registry, CalibrationEngine calibration) { _registry = registry; _calibration = calibration; }

    public void RecordOutcome(DecisionCase c, Outcome o)
    {
        c.Outcome = o;
        string domain = c.Profile?.Domain ?? c.Request.Domain;
        c.Add($"[eval] 真實結果（{o.Level}）：{o.Note}");

        // 1) Solver：他最有信心的假設是不是真的
        foreach (var run in c.AgentRuns.Where(r => r.Role == "solver" && r.Ok))
        {
            var top = c.Claims.Where(k => k.Kind == ClaimKind.Hypothesis)
                .Select(k => (k, p: k.Proposals.FirstOrDefault(pr => pr.AgentId == run.AgentId)))
                .Where(x => x.p is not null).MaxBy(x => x.p!.StatedConfidence);
            if (top.k is null) continue;
            bool success = o.ClaimTruth.GetValueOrDefault(top.k.Id, false);
            _registry.RecordResult(c.Id, run.AgentId, domain, "solver", success, o.Level);
            c.Add($"[eval] {run.AgentId} 首選 {top.k.Id} → {(success ? "✓" : "✗")}");
        }

        // 2) 校準：每個（agent, 自報信心）對上主張真假
        foreach (var claim in c.Claims.Where(k => k.Kind == ClaimKind.Hypothesis))
            foreach (var p in claim.Proposals)
                _calibration.Record(p.AgentId, p.StatedConfidence, o.ClaimTruth.GetValueOrDefault(claim.Id, false));

        // 3) Critic：把真假設打成高嚴重度 = 錯；把假假設打成低嚴重度 = 錯
        foreach (var v in c.Verifications.Where(v => v.Level == VerifierLevel.L1_LlmCritic && v.ClaimId is not null))
        {
            string agent = v.VerifierId.Split(':').Last();
            bool truth = o.ClaimTruth.GetValueOrDefault(v.ClaimId!, false);
            bool success = truth ? v.Pass == true : v.Pass == false;
            _registry.RecordResult(c.Id, agent, domain, "critic", success, o.Level);
        }

        // 4) 實驗設計者：登記的似然表在觀察到的結果欄，是否把真假設排在最高
        if (o.TrueClaimId is not null)
            foreach (var exp in c.Experiments.Where(e => e.ObservedOutcome is int))
            {
                int obs = exp.ObservedOutcome!.Value;
                var best = exp.LikelihoodByClaim.Where(kv => obs < kv.Value.Length).MaxBy(kv => kv.Value[obs]);
                bool success = best.Key == o.TrueClaimId;
                _registry.RecordResult(c.Id, exp.DesignedBy, domain, "experiment_designer", success, o.Level);
                c.Add($"[eval] {exp.Id}（{exp.DesignedBy}）區分力 → {(success ? "✓" : "✗")}");
            }
    }

    public string Report() => "── Agent 表現（Beta 後驗）──\n" + _registry.Report() + "\n── 校準 ──\n" + _calibration.Report();
}
