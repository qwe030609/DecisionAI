// ============================================================================
//  Mutation switches（Rev1 §D8）：把守門件換成壞的，benchmark 必須變紅。
//  這是「檢查是否真的有效」的唯一證據——通過率本身證明不了任何事。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Journal;
using DecisionAI.Modules.Routing;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

/// <summary>M1：Triage 永遠放行 —— 等同 ChatGPT 版的 route()，沒有拒答通道。</summary>
public sealed class AlwaysProceedTriage : IVerifiabilityTriage
{
    public TriageResult Assess(DecisionRequest req, VerifierInventory inv)
    {
        string domain = string.IsNullOrWhiteSpace(req.Domain) ? "general" : req.Domain;
        var facts = new ProblemFacts(inv.Best, req.Reflexive, req.Probabilistic, req.ActionsOrEmpty.Length > 0,
                                     Math.Max(1, req.StagesOrEmpty.Length), domain, req.Constraints.RiskLevel, req.GroundTruth);
        return new TriageResult(true, ReasonCode.None, "（壞的守門件）一律放行", null, facts);
    }
}

/// <summary>M2：拒答閘永不拒答 —— 流程中止也照樣背書。</summary>
public sealed class NeverAbstainGate : IAbstentionGate
{
    public AbstentionLayer Evaluate(CaseState s) => AbstentionLayer.NotAbstained;
}

/// <summary>M3：能力層浮報 —— 不管通過哪一級，上限都寫 0.95，並宣稱有覆蓋保證。</summary>
public sealed class OverclaimingAssurance : IAssuranceService
{
    public AssuranceReport Build(CaseState s) => new(
        new CapabilityLayer(VerifierLevel.L4_Experiment, 0.95, false),
        new CoverageLayer(0.90, new[] { "（壞的守門件）憑空宣稱的預測集合" }),
        s.Decision is { } d ? new StabilityLayer(d.Robustness, d.WorstCase, d.MaxRegret) : null,
        AbstentionLayer.NotAbstained);
}

/// <summary>M4：證據直接拼進 prompt，不中性化、不掃描注入。</summary>
public sealed class RawEvidenceGuard : IInjectionGuard
{
    public EvidenceRenderResult Render(IEnumerable<Core.Domain.Evidence> evidence)
        => new(string.Join("\n", evidence.Select(e => $"{e.Id} {e.Content}")), ImmutableArray<string>.Empty);
}

/// <summary>M5：越權的 critic —— 一邊批判一邊自己補一條主張（Goodhart 的典型破法）。</summary>
public sealed class RogueCriticVerifier : IVerifier
{
    public string Id => "rogue-critic";
    public VerifierLevel Level => VerifierLevel.L2_Rule;   // 掛在 L2 這一步，確保每個案子都會跑到

    public Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState s, string stepId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CaseEvent>>(new CaseEvent[]
        {
            new ClaimProposed("H99", ClaimKind.Hypothesis, "（壞的守門件）評審自己塞進來的主張", 0.95,
                              ImmutableArray<string>.Empty, ImmutableArray<string>.Empty).By(stepId, "critic-D", Actors.Critic)
        });
}

/// <summary>Affected：這個 mutation 在哪些案例上「有機會」改變行為。抓到率要用 detected/affected 看，不是 /32。</summary>
public sealed record Mutation(string Id, string Name, string Target, Func<TestSystemOptions, TestSystemOptions> Apply,
                              Func<BenchCase, bool> Affected, string AffectedWhy);

public static class Mutations
{
    public static ImmutableArray<Mutation> All => ImmutableArray.Create(
        new Mutation("M1", "Triage 永遠放行（移除拒答通道）", "IVerifiabilityTriage",
            o => o with { Triage = new AlwaysProceedTriage() },
            c => c.Expected.Disposition == Disposition.Abstain, "只有應被拒答的案例會改變行為"),
        new Mutation("M2", "拒答閘永不拒答（中止也背書）", "IAbstentionGate",
            o => o with { Gate = new NeverAbstainGate() },
            c => c.Expected.Disposition == Disposition.Abstain, "只有應被拒答的案例會改變行為"),
        new Mutation("M3", "能力層浮報上限並宣稱覆蓋保證", "IAssuranceService",
            o => o with { Assurance = new OverclaimingAssurance() },
            _ => true, "每個案例的 Assurance 都被浮報"),
        new Mutation("M4", "證據不中性化直接進 prompt", "IInjectionGuard",
            o => o with { Guard = new RawEvidenceGuard() },
            c => c.Expected.Disposition == Disposition.Proceed, "拒答案例不會呼叫 LLM，沒有 prompt 可汙染"),
        new Mutation("M5", "越權 critic 自己提主張 + 權限矩陣全開", "IRolePermission",
            o => o with { Permission = new AlwaysAllowPermission(), ExtraVerifiers = _ => new IVerifier[] { new RogueCriticVerifier() } },
            c => c.Expected.Disposition == Disposition.Proceed, "拒答案例不會跑到驗證步驟")
    );
}
