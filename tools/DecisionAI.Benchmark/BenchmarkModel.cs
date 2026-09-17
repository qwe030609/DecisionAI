// ============================================================================
//  Benchmark 案例模型 — 把「八維小數 profile」換成 Rev2 的事實欄位。
//  Rev2 §4.2 的硬性要求：ProblemFacts 只放事實與類別，不得出現 LLM 自估的小數。
//  所以這裡登記的是「現場實際附了哪一級 verifier」「ground truth 是否有共識」
//  「這題是否屬於某個專業數值模型的領域」，而不是「可驗證性 = 0.95」。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;

namespace DecisionAI.Benchmark;

public enum Disposition { Proceed, Abstain }

/// <summary>金標期望：先宣告，再比對。不從實際結果反推（否則檢查恆真）。</summary>
public sealed record Expected(Disposition Disposition, ReasonCode Code = ReasonCode.None,
                              Strategy? Strategy = null, string? Workflow = null, bool ExpectCritic = false)
{
    public static Expected Abstain(ReasonCode code) => new(Disposition.Abstain, code);
    public static Expected Proceed(Strategy s, string wf, bool critic = false)
        => new(Disposition.Proceed, ReasonCode.None, s, wf, critic);
}

/// <summary>一個候選故障模式：四元組 + 定性信心 + 證據引用（1-based 索引）。</summary>
public sealed record HypSpec(ClaimFrame Frame, LikertBelief Confidence, int[] For, int[] Against);

public sealed record BenchCase
{
    public required string Id { get; init; }
    public required string Dimension { get; init; }
    public required string Level { get; init; }
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public required string Domain { get; init; }

    // ── Rev2 事實（取代八維小數）──
    public GroundTruthStatus GroundTruth { get; init; } = GroundTruthStatus.Agreed;
    public bool Reflexive { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Medium;
    public bool HasL3 { get; init; }                      // 現場附了可執行測試 / 模擬器
    public bool HasL4 { get; init; }                      // 現場附了實驗環境（HIL / staging / A-B）
    public SpecialistDomain? Specialist { get; init; }    // ★ 屬專業數值模型領域 → Tool/Model Router 轉介
    public ImmutableArray<StageSpec> Stages { get; init; } = ImmutableArray<StageSpec>.Empty;
    public ImmutableArray<ActionOption> Actions { get; init; } = ImmutableArray<ActionOption>.Empty;
    public ImmutableArray<EvidenceDraft> Evidence { get; init; } = ImmutableArray<EvidenceDraft>.Empty;
    public ImmutableArray<HypSpec> Hypotheses { get; init; } = ImmutableArray<HypSpec>.Empty;

    /// <summary>Catalog 種子：已知的故障模式（等同這個領域的 FMEA 表）。空 = 這個領域還沒有記憶。</summary>
    public ImmutableArray<ClaimFrame> CatalogSeed { get; init; } = ImmutableArray<ClaimFrame>.Empty;

    public int TrueHypothesis { get; init; } = 1;         // 模擬器隱藏的真相（1-based）

    public required Expected Expected { get; init; }
    public string WhyRev2 { get; init; } = "";
    public double Budget { get; init; } = 4.0;
}
