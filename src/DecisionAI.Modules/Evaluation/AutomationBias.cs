// ============================================================================
//  AutomationBiasMonitor（Rev2 §9「HITL 僅部分覆蓋」的歸宿 / Phase 3）
//
//  「有人類核准」這件事本身不構成監督。橡皮圖章化是可量測的：
//  接受率逼近 100%、決策時間短到不可能讀完證據、每次都是先看到系統建議才表態。
//  三個訊號都不是道德問題，是流程設計的失效——所以處置也在流程上：
//  收緊呈現方式（強制盲評先行）與要求第二位核准者，而不是寫一份提醒大家認真看的文件。
//
//  這是跨案的監控，所以它有自己的滾動視窗，不屬於單一 case 的狀態。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Evaluation;

/// <summary>一次人類決策的紀錄。Seconds 由 gateway 回報——只有它知道人看了多久。</summary>
public sealed record HumanDecision(string CaseId, HumanRole Role, string Approver, bool Approved,
                                   double? Seconds, bool SawRecommendationFirst);

/// <summary>監控的產出：一組要收緊的流程要求。</summary>
public sealed record OversightPolicy(bool RequireBlindFirstPass, bool RequireSecondApprover, ImmutableArray<string> Reasons)
{
    public static readonly OversightPolicy Normal =
        new(false, false, ImmutableArray<string>.Empty);
    public bool Tightened => RequireBlindFirstPass || RequireSecondApprover;
}

public interface IAutomationBiasMonitor
{
    void Record(HumanDecision decision);
    OversightPolicy PolicyFor(HumanRole role);
    string Report();
}

public sealed class AutomationBiasMonitor : IAutomationBiasMonitor
{
    private readonly List<HumanDecision> _log = new();
    private readonly object _lock = new();

    /// <summary>少於這個數不判定：三次全同意不能說明任何事。</summary>
    public int MinDecisions { get; init; } = 10;

    /// <summary>接受率高於此值視為橡皮圖章的徵兆（不是證據——所以處置是收緊流程，不是推翻決定）。</summary>
    public double RubberStampRate { get; init; } = 0.95;

    /// <summary>決策時間中位數低於此秒數，代表不可能讀完證據。</summary>
    public double TooFastSeconds { get; init; } = 5;

    /// <summary>「先看到系統建議才表態」的比例上限。</summary>
    public double RecommendationFirstLimit { get; init; } = 0.50;

    public void Record(HumanDecision d) { lock (_lock) _log.Add(d); }

    public OversightPolicy PolicyFor(HumanRole role)
    {
        List<HumanDecision> rows;
        lock (_lock) rows = _log.Where(d => d.Role == role).ToList();
        if (rows.Count < MinDecisions) return OversightPolicy.Normal;

        double accept = rows.Count(d => d.Approved) / (double)rows.Count;
        var times = rows.Where(d => d.Seconds is not null).Select(d => d.Seconds!.Value).OrderBy(x => x).ToList();
        double? median = times.Count == 0 ? null : times[times.Count / 2];
        double first = rows.Count(d => d.SawRecommendationFirst) / (double)rows.Count;

        var reasons = ImmutableArray.CreateBuilder<string>();
        bool tooFast = median is { } m && m < TooFastSeconds;
        if (accept >= RubberStampRate) reasons.Add($"接受率 {accept:P0}（{rows.Count} 次）——實質上沒有否決過任何事");
        if (tooFast) reasons.Add($"決策時間中位數 {median:F1} 秒——不可能讀完證據");
        if (first > RecommendationFirstLimit) reasons.Add($"{first:P0} 的決策是先看到系統建議才表態");

        if (reasons.Count == 0) return OversightPolicy.Normal;

        // 有徵兆就一定要有動作。只記一筆「接受率 100%」然後什麼都不做，
        // 跟沒有監控是同一件事——而且更糟，因為報表上會顯示「已監控」。
        // 最便宜的處置是先把建議收起來，所以它是預設動作；
        // 要求第二位核准者比較貴，留給「又快又照單全收」這種最明確的情況。
        return new OversightPolicy(
            RequireBlindFirstPass: true,
            RequireSecondApprover: accept >= RubberStampRate && tooFast,
            Reasons: reasons.ToImmutable());
    }

    public string Report()
    {
        List<HumanDecision> rows;
        lock (_lock) rows = _log.ToList();
        if (rows.Count == 0) return "沒有人類決策紀錄";
        return string.Join("\n", rows.GroupBy(d => d.Role).OrderBy(g => g.Key).Select(g =>
        {
            var t = g.Where(d => d.Seconds is not null).Select(d => d.Seconds!.Value).OrderBy(x => x).ToList();
            return $"{g.Key}：{g.Count()} 次，接受率 {g.Count(d => d.Approved) / (double)g.Count():P0}，" +
                   $"時間中位數 {(t.Count == 0 ? "—" : t[t.Count / 2].ToString("F1") + " 秒")}，" +
                   $"先看建議 {g.Count(d => d.SawRecommendationFirst) / (double)g.Count():P0}";
        }));
    }
}

/// <summary>Mutation switch：不監控，永遠回報流程正常。</summary>
public sealed class BlindOversightMonitor : IAutomationBiasMonitor
{
    public void Record(HumanDecision decision) { }
    public OversightPolicy PolicyFor(HumanRole role) => OversightPolicy.Normal;
    public string Report() => "（壞的守門件）不監控人類決策";
}
