// ============================================================================
//  序列型情境（Phase 3 的完成門檻）
//
//  前兩期的測試都是單案的：跑一個 case，斷言它的行為。但「學習」這件事在單案上
//  根本看不出來——一個 case 的 Beta 後驗、一個 case 的校準曲線都沒有意義。
//  所以 Phase 3 的門檻是序列型的：連跑 N 個 case，看四條曲線走對方向沒有。
//
//  關鍵設計：每個 agent 有一個「真實能力」參數（例如 0.80），
//  而它在某個 case 上答對與否由 (agentId, caseId) 的穩定雜湊決定。
//  這讓整串序列完全可重放，同時又讓系統必須真的從結果裡把那個數字學回來——
//  這是唯一能驗證「學習有沒有在學」的辦法：先知道正確答案，再看它收斂到哪裡。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Catalog;

namespace DecisionAI.Testing;

/// <summary>一個 agent 的宣告能力：Accuracy 是「真實」值，系統不知道，要自己學。</summary>
public sealed record SeqAgent(string Id, string Vendor, string Family, double Accuracy, LikertBelief Confidence);

public sealed record SeqCase(string Id, string TrueMechanism, ImmutableArray<string> Mechanisms);

public static class SequentialScenario
{
    public const string Domain = "seq_diagnosis";

    /// <summary>五種故障機制輪流當真相。位置固定對應機制，讓 Catalog 命中率可以被量。</summary>
    public static readonly ImmutableArray<string> Pool = ImmutableArray.Create(
        Mechanisms.RaceCondition, Mechanisms.LifecycleMisuse, Mechanisms.EnvironmentalStress,
        Mechanisms.ResourceLeak, Mechanisms.ConfigurationError);

    public static string LocusOf(string mechanism) => $"subsystem-{mechanism}";

    public static ClaimFrame FrameOf(string mechanism) => new(
        mechanism, LocusOf(mechanism),
        $"{mechanism} 的典型觸發條件", $"{mechanism} 在紀錄上的可觀察徵象");

    /// <summary>
    /// 三個 agent：能力高但極度自信、能力中等且誠實、能力差。
    /// 第一個刻意設成「說幾乎確定但只有六成對」——校準要學的就是這件事。
    /// </summary>
    public static ImmutableArray<SeqAgent> Agents => ImmutableArray.Create(
        new SeqAgent("solver-hi",   "vendor-x", "x-large",  0.85, LikertBelief.Likely),
        new SeqAgent("solver-loud", "vendor-y", "y-large",  0.60, LikertBelief.AlmostCertain),
        new SeqAgent("solver-low",  "vendor-z", "z-medium", 0.35, LikertBelief.EvenOdds));

    public static ImmutableArray<SeqCase> Cases(int n)
    {
        var b = ImmutableArray.CreateBuilder<SeqCase>();
        for (int i = 0; i < n; i++)
        {
            string truth = Pool[i % Pool.Length];
            // 每案三個候選：真相 + 兩個鄰居。候選集合隨案輪轉，Catalog 才會逐步長出來
            var mechs = ImmutableArray.Create(truth, Pool[(i + 1) % Pool.Length], Pool[(i + 2) % Pool.Length]);
            b.Add(new SeqCase($"SEQ-{i:000}", truth, mechs));
        }
        return b.ToImmutable();
    }

    public static DecisionRequest Request(SeqCase c) => new(
        Problem: $"{c.Id}：系統出現間歇性失效，請分析根因",
        Goal: "找出根因",
        Domain: Domain,
        Constraints: new Constraints(Budget: 3.0, MaxLatencySeconds: 120, RiskLevel: RiskLevel.Medium),
        GroundTruth: GroundTruthStatus.Agreed);

    public static IReadOnlyList<EvidenceDraft> Evidence(SeqCase c) => new[]
    {
        new EvidenceDraft(EvidenceSource.RuntimeLog, "runtime", $"{c.Id} 的執行紀錄：失效前後的狀態轉移"),
        new EvidenceDraft(EvidenceSource.SourceCode, "repo",    $"{c.Id} 相關模組的原始碼片段"),
        new EvidenceDraft(EvidenceSource.Sensor,     "sensor",  $"{c.Id} 期間的環境量測"),
    };

    /// <summary>
    /// 答對與否由 (agentId, caseId) 的穩定雜湊決定：整串序列可重放，
    /// 而長期的命中率會收斂到宣告的 Accuracy。
    /// </summary>
    public static bool IsCorrectOn(SeqAgent a, string caseId)
        => Fraction(a.Id + "|" + caseId) < a.Accuracy;

    internal static double Fraction(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in s) { h ^= ch; h *= 16777619; }
            return (h % 100000) / 100000.0;
        }
    }

    /// <summary>
    /// 腳本人格：從 prompt 取出 case id，決定這次把哪一個機制排第一。
    /// 它「不知道」自己對不對——只是照自己的習慣給同一個信心等級，
    /// 所以過度自信的 agent 會在校準曲線上現形。
    /// </summary>
    public static ScriptedLlm Persona(SeqAgent agent, IReadOnlyDictionary<string, SeqCase> byId)
        => new(agent.Family, req =>
        {
            var c = byId[req.Key.CaseId];
            bool correct = IsCorrectOn(agent, c.Id);
            var ordered = correct
                ? c.Mechanisms
                : ImmutableArray.Create(c.Mechanisms[1], c.Mechanisms[0], c.Mechanisms[2]);

            return req.Role switch
            {
                "solver" => Hypotheses(ordered, agent.Confidence),
                "experiment_designer" => """{"experiments":[]}""",
                _ => """{"critiques":[]}"""
            };
        });

    private static string Hypotheses(ImmutableArray<string> mechanisms, LikertBelief top)
    {
        var arr = new JsonArray();
        for (int i = 0; i < mechanisms.Length; i++)
        {
            var f = FrameOf(mechanisms[i]);
            arr.Add(new JsonObject
            {
                ["mechanism"] = f.Mechanism,
                ["locus"] = f.Locus,
                ["trigger"] = f.Trigger,
                ["observable"] = f.Observable,
                ["confidence"] = (i == 0 ? top : Likert.Shift(top, -1 - i)).ToString(),
                ["evidenceFor"] = new JsonArray($"EV-{i + 1:000}"),
                ["evidenceAgainst"] = new JsonArray()
            });
        }
        return new JsonObject { ["hypotheses"] = arr }.ToJsonString();
    }

    /// <summary>L3 模擬器：真機制通過、其餘不過。這是序列裡唯一「知道真相」的東西。</summary>
    public static Func<Claim, CaseState, (bool, double, string)?> Verifier(IReadOnlyDictionary<string, SeqCase> byId)
        => (claim, s) =>
        {
            if (claim.Kind != ClaimKind.Hypothesis || claim.IsOpinion) return null;
            var c = byId[s.Id];
            return claim.Frame.Mechanism == c.TrueMechanism
                ? (true, 0.9, "可執行測試：在受控條件下重現，注入修復後歸零")
                : (false, 0.2, "可執行測試：受控條件下無法重現");
        };

    /// <summary>真實結果：Outcome 以「真機制對應的本地編號」表示。</summary>
    public static Outcome OutcomeFor(SeqCase c, CaseState s)
    {
        string? trueId = s.Claims.FirstOrDefault(k => k.Frame.Mechanism == c.TrueMechanism)?.LocalId;
        var truth = s.Claims.ToImmutableDictionary(k => k.LocalId, k => k.LocalId == trueId);
        return new Outcome(trueId, truth, VerifierLevel.L5_RealOutcome, $"{c.Id} 的真根因是 {c.TrueMechanism}");
    }
}
