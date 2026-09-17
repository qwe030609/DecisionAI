// ============================================================================
//  Mutation switches（Rev2 §6.3）：把守門件換成壞的，benchmark 必須變紅。
//  這是「檢查是否真的有效」的唯一證據——通過率本身證明不了任何事。
//  M6–M10 是 Rev2 新增的守門件。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Assurance;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Journal;
using DecisionAI.Modules.Routing;
using DecisionAI.Modules.Verification;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

/// <summary>Affected：這個 mutation 在哪些案例上「有機會」改變行為。抓到率要用 detected/affected 看。</summary>
public sealed record Mutation(string Id, string Name, string Target, Func<TestSystemOptions, TestSystemOptions> Apply,
                              Func<BenchCase, bool> Affected, string AffectedWhy, bool IsRev2New = false,
                              string? NotObservableHere = null);

/// <summary>M5 用：越權的 critic —— 一邊批判一邊自己補一條主張（Goodhart 的典型破法）。</summary>
public sealed class RogueCriticVerifier : DecisionAI.Core.Ports.IVerifier
{
    public string Id => "rogue-critic";
    public DecisionAI.Core.Domain.VerifierLevel Level => DecisionAI.Core.Domain.VerifierLevel.L2_Rule;

    public Task<IReadOnlyList<DecisionAI.Core.Journal.CaseEvent>> VerifyAsync(
        DecisionAI.Core.Journal.CaseState s, string stepId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DecisionAI.Core.Journal.CaseEvent>>(new DecisionAI.Core.Journal.CaseEvent[]
        {
            new DecisionAI.Core.Journal.ClaimProposed("H99",
                new DecisionAI.Core.Domain.ClaimKey("DRAFT-rogue"),
                DecisionAI.Core.Domain.ClaimKind.Hypothesis,
                new DecisionAI.Core.Domain.ClaimFrame(DecisionAI.Core.Domain.Mechanisms.RaceCondition,
                    "（壞的守門件）評審自己塞進來的主張", "無", "無"),
                DecisionAI.Core.Domain.LikertBelief.AlmostCertain,
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)
                .By(stepId, "critic-D", DecisionAI.Core.Journal.Actors.Critic)
        });
}

public static class Mutations
{
    private static bool Proceeds(BenchCase c) => c.Expected.Disposition == Disposition.Proceed;
    private static bool Abstains(BenchCase c) => c.Expected.Disposition == Disposition.Abstain;

    /// <summary>需要 briefer 生成 payload 的拒答（其餘是確定性 payload，不呼叫模型）。</summary>
    private static bool NeedsBriefer(BenchCase c) => c.Expected.Code
        is DecisionAI.Core.Assurance.ReasonCode.ReflexiveDirection
        or DecisionAI.Core.Assurance.ReasonCode.NoGroundTruth
        or DecisionAI.Core.Assurance.ReasonCode.DefinitionalDispute;

    public static ImmutableArray<Mutation> All => ImmutableArray.Create(
        new Mutation("M1", "Triage 永遠放行（移除拒答通道）", "IVerifiabilityTriage",
            o => o with { Triage = new AlwaysProceedTriage() },
            c => Abstains(c) && c.Expected.Code != DecisionAI.Core.Assurance.ReasonCode.DelegatedToExternalModel,
            "只有被 Triage 攔下的案例會改變行為（轉介是下一段做的）"),

        new Mutation("M2", "拒答閘永不拒答（中止也背書）", "IAbstentionGate",
            o => o with { Gate = new NeverAbstainGate() },
            Abstains, "只有應被拒答的案例會改變行為"),

        new Mutation("M3", "能力層浮報並宣稱覆蓋保證", "IAssuranceService",
            o => o with { Assurance = new OverclaimingAssurance() },
            _ => true, "每個案例的 Assurance 都被浮報"),

        new Mutation("M4", "證據不中性化直接進 prompt", "IInjectionGuard",
            o => o with { Guard = new RawEvidenceGuard() },
            c => Proceeds(c) || (Abstains(c) && NeedsBriefer(c)),
            "轉介與無 verifier 的拒答是確定性 payload，不呼叫模型，也就沒有 prompt 可汙染"),

        new Mutation("M5", "越權 critic 自己提主張 + 權限矩陣全開", "IRolePermission",
            o => o with { Permission = new AlwaysAllowPermission(), ExtraVerifiers = _ => new[] { (DecisionAI.Core.Ports.IVerifier)new RogueCriticVerifier() } },
            Proceeds, "拒答案例不會跑到驗證步驟"),

        // ── Rev2 新增的守門件 ──
        new Mutation("M6", "Canonicalizer 永遠回新條目", "IClaimCanonicalizer",
            o => o with { Canonicalizer = new AlwaysNewCanonicalizer() },
            c => Proceeds(c) && !c.CatalogSeed.IsEmpty,
            "只有領域已經累積了 Catalog 條目的案例，才有跨 case 記憶可以破壞", IsRev2New: true),

        new Mutation("M7", "拿掉預登記 hash 比對", "IPreRegistrationGuard",
            o => o with { PreRegGuard = new NoOpPreRegistrationGuard() },
            _ => false,
            "腳本世界不會竄改似然，所以這個守門件在本 benchmark 上不可觀察", IsRev2New: true,
            NotObservableHere: "以單元測試 Category1_TamperedLikelihood_IsRejectedBeforeRunning 覆蓋：竄改似然後守衛丟例外並拒跑"),

        new Mutation("M8", "停用情境對稱性檢查（搭配會寫歪的 briefer）", "IPayloadVerifier",
            o => o with { PayloadVerifier = new PermissivePayloadVerifier() },
            c => Abstains(c) && c.Reflexive,
            "只有需要 ScenarioBriefing 的反身案例會出現不對稱", IsRev2New: true),

        new Mutation("M9", "Tool/Model Router 從不轉介", "IToolModelRouter",
            o => o with { ToolRouter = new NeverDelegateRouter() },
            c => c.Specialist is not null,
            "只有登記了專業數值模型領域的案例受影響", IsRev2New: true),

        new Mutation("M10", "在單一模型家族環境下忽略降級階梯", "IRoleAssigner",
            o => o with { IgnoreDegradation = true },
            Proceeds, "把模型池壓成單一家族後測；拒答案例不會走到角色指派", IsRev2New: true),

        new Mutation("M11", "critic 直接看 solver 的原始輸出", "CriticContextBuilder",
            o => o with
            {
                CriticContext = (s, guard) => CriticContextBuilder.Build(s, guard) + "\n\nsolver 原始輸出：\n" +
                    string.Join("\n", s.AgentRuns.Where(r => r.Role == "solver").Select(r => r.RawOutput))
            },
            c => Proceeds(c) && c.Expected.ExpectCritic,
            "只有實際跑 L1 critic 的案例受影響", IsRev2New: true)
    );
}

/// <summary>M4 用：證據直接拼進 prompt，不中性化、不掃描注入。</summary>
public sealed class RawEvidenceGuard : DecisionAI.Core.Ports.IInjectionGuard
{
    public DecisionAI.Core.Ports.EvidenceRenderResult Render(IEnumerable<DecisionAI.Core.Domain.Evidence> evidence)
        => new(string.Join("\n", evidence.Select(e => $"{e.Id} {e.Content}")), ImmutableArray<string>.Empty);
}
