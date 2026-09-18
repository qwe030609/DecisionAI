// ============================================================================
//  ClaimFrame — Rev2 D9 的核心：LLM 只提名（nominate），不自由寫句子。
//  主張被強制成四元組，比對發生在欄位層級，不在自然語言層級：
//  這一步就把「每次換句話說」的表述變異消掉，而且不依賴中文分詞。
// ============================================================================

using System.Security.Cryptography;
using System.Text;

namespace DecisionAI.Core.Domain;

/// <summary>
/// Mechanism 是封閉集（對應 FMEA 故障模式庫）。新增條目需經 arbiter 核准，
/// 不能由 LLM 自己造詞——否則 Catalog 會被同義詞灌爆，跨 case 記憶就失效。
/// </summary>
public static class Mechanisms
{
    public const string RaceCondition       = "race_condition";
    public const string ResourceLeak        = "resource_leak";
    public const string LifecycleMisuse     = "lifecycle_misuse";
    public const string ProtocolViolation   = "protocol_violation";
    public const string TimingDrift         = "timing_drift";
    public const string ConfigurationError  = "configuration_error";
    public const string EnvironmentalStress = "environmental_stress";
    public const string WearOut             = "wear_out";
    public const string BlockingIo          = "blocking_io";
    public const string AlgorithmicChoice   = "algorithmic_choice";
    public const string ModelSpecification  = "model_specification";
    public const string SpecificationFact   = "specification_fact";
    public const string DesignTradeoff      = "design_tradeoff";
    public const string StageDeliverable    = "stage_deliverable";   // 流水線段落交付物

    private static readonly HashSet<string> Closed = new(StringComparer.Ordinal)
    {
        RaceCondition, ResourceLeak, LifecycleMisuse, ProtocolViolation, TimingDrift,
        ConfigurationError, EnvironmentalStress, WearOut, BlockingIo, AlgorithmicChoice,
        ModelSpecification, SpecificationFact, DesignTradeoff, StageDeliverable
    };

    public static bool IsKnown(string m) => Closed.Contains(m);
    public static IReadOnlyCollection<string> All => Closed;
}

/// <summary>
/// 四元組。Observable 是刻意設計的約束：說不出可觀察現象的假設，
/// 本質上不是可驗證的假設，schema 階段就該擋掉。
/// </summary>
public sealed record ClaimFrame(string Mechanism, string Locus, string Trigger, string Observable)
{
    public bool IsWellFormed => SchemaError is null;

    public string? SchemaError =>
        !Mechanisms.IsKnown(Mechanism)   ? $"Mechanism「{Mechanism}」不在封閉集內（新增需經 arbiter）"
        : Locus.Trim().Length == 0       ? "Locus 為空：說不出發生在哪裡"
        : Trigger.Trim().Length == 0     ? "Trigger 為空：說不出什麼條件下觸發"
        : Observable.Trim().Length == 0  ? "Observable 為空：說不出可觀察現象 → 不是可驗證的假設"
        : null;

    /// <summary>正規化：大小寫、空白、全形括號一律拉平，避免措辭差異造成假的新條目。</summary>
    public static string Norm(string s)
        => string.Join(" ", s.ToLowerInvariant()
            .Replace('（', '(').Replace('）', ')').Replace('　', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string Identity => $"{Mechanism}|{Norm(Locus)}";
    public string FullIdentity => $"{Mechanism}|{Norm(Locus)}|{Norm(Trigger)}|{Norm(Observable)}";

    /// <summary>
    /// 未進 Catalog 前的決定性草稿 id。刻意用 Identity（Mechanism|Locus）而不是四欄全部：
    /// 主張的身分是「哪個機制發生在哪裡」，Trigger / Observable 是描述細節。
    /// 用四欄算 id 會讓「換句話說」變成一條新主張——那正是 Rev1 的毛病。
    /// </summary>
    public string DraftId => "DRAFT-" + Hash(Identity)[..8];

    public static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public string Render() => $"[{Mechanism}] {Locus}｜觸發：{Trigger}｜可觀察：{Observable}";
}

/// <summary>跨 case 穩定的識別。Catalog 命中時是 catalog id，未命中時是 DraftId。</summary>
public readonly record struct ClaimKey(string Value)
{
    public bool IsCatalogued => !Value.StartsWith("DRAFT-", StringComparison.Ordinal);
    public override string ToString() => Value;
}

/// <summary>
/// 定性等級取代小數。LLM 對「0.83 還是 0.87」沒有跨次穩定性，
/// 但對「很可能 vs 一半一半」的一致性高得多；離散化把抽樣雜訊擋在外面。
/// </summary>
public enum LikertBelief { AlmostImpossible = 0, Unlikely = 1, EvenOdds = 2, Likely = 3, AlmostCertain = 4 }

public static class Likert
{
    /// <summary>等級 → 機率，確定性查表。這張表是程式的，不是 LLM 的。</summary>
    public static double ToProbability(LikertBelief b) => b switch
    {
        LikertBelief.AlmostCertain    => 0.90,
        LikertBelief.Likely           => 0.75,
        LikertBelief.EvenOdds         => 0.50,
        LikertBelief.Unlikely         => 0.25,
        LikertBelief.AlmostImpossible => 0.10,
        _ => 0.50
    };

    public static bool TryParse(string? s, out LikertBelief b) => Enum.TryParse(s, ignoreCase: true, out b);

    /// <summary>多個異質 agent 各投等級，取中位數（抗離群優於平均）。偶數個取上中位數以維持決定性。</summary>
    public static LikertBelief Median(IEnumerable<LikertBelief> votes)
    {
        var v = votes.Select(x => (int)x).OrderBy(x => x).ToList();
        return v.Count == 0 ? LikertBelief.EvenOdds : (LikertBelief)v[v.Count / 2];
    }

    /// <summary>敏感度分析用：整體 ±1 級。</summary>
    public static LikertBelief Shift(LikertBelief b, int delta) => (LikertBelief)Math.Clamp((int)b + delta, 0, 4);

    public static string Label(LikertBelief b) => b switch
    {
        LikertBelief.AlmostCertain    => "幾乎確定",
        LikertBelief.Likely           => "很可能",
        LikertBelief.EvenOdds         => "一半一半",
        LikertBelief.Unlikely         => "不太可能",
        _                             => "幾乎不可能"
    };
}
