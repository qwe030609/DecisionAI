// ============================================================================
//  DriftAlarm（Rev2 §9「校準漂移無告警」的歸宿 / Phase 3）
//
//  模型會換版本、領域會變、題目分佈會漂。一個曾經校準良好的 agent，
//  三個月後可能整個偏掉——而系統對此完全無感，因為它只會看到一個「平均 Brier」，
//  而平均會把最近的惡化稀釋掉。
//
//  所以這裡比較的是「近期一半 vs 早期一半」，不是整體平均。
//  偵測到漂移時的處置刻意不是「降低權重」而是「提高人類核准要求」：
//  權重是學出來的，漂移的時候學習訊號本身就不可信，再用它去調權重只是更快地學壞。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Policy;

namespace DecisionAI.Modules.Evaluation;

public sealed record DriftSignal(string Agent, double EarlyBrier, double RecentBrier, int Samples, string Detail);

public sealed record DriftVerdict(ImmutableArray<DriftSignal> Drifting, string Detail)
{
    public bool Any => !Drifting.IsDefaultOrEmpty && Drifting.Length > 0;
    public static readonly DriftVerdict None = new(ImmutableArray<DriftSignal>.Empty, "沒有偵測到校準漂移");
}

public interface IDriftAlarm
{
    DriftVerdict Check(PolicySnapshot policy);
}

public sealed class DriftAlarm : IDriftAlarm
{
    /// <summary>
    /// 樣本少於這個數就不告警：小樣本的「惡化」多半是雜訊。
    ///
    /// 這個數字被序列型測試打臉過一次：原本設 12，結果在一段完全沒有漂移的 40 案序列上
    /// 誤報了 6 次。原因是校準點是「每個假設一筆」，同一案的三筆其實高度相關，
    /// 12 筆只等於四個 case——用四個 case 判定一個模型漂了，那是在量雜訊。
    /// 30 筆（約十案）之後才判定，誤報就消失了。
    /// </summary>
    public int MinSamples { get; init; } = 30;

    /// <summary>近期 Brier 比早期差超過這個量才算漂移（Brier 越小越好）。</summary>
    public double Worsening { get; init; } = 0.08;

    /// <summary>
    /// 設計效應：校準點是「每案每條假設一筆」，同一案的幾筆來自同一次排序，彼此高度相關。
    /// 把它們當成獨立樣本會低估標準誤——這是叢集抽樣的典型錯誤，
    /// 症狀就是「在完全沒有漂移的序列上偶爾告警」。
    /// </summary>
    public double DesignEffect { get; init; } = 3.0;

    public DriftVerdict Check(PolicySnapshot policy)
    {
        var signals = ImmutableArray.CreateBuilder<DriftSignal>();
        foreach (var (agent, curve) in policy.Calibration.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var r = curve.Records;
            if (r.Length < MinSamples) continue;

            int half = r.Length / 2;
            var earlyRows = r.Take(half).ToList();
            var recentRows = r.Skip(half).ToList();
            double early = Brier(earlyRows);
            double recent = Brier(recentRows);
            double diff = recent - early;
            if (diff <= Worsening) continue;

            // 絕對門檻不夠：兩半各自都有抽樣誤差，光看差值會把雜訊當成漂移。
            // 這裡要求差值同時超過「兩半差的 2 個標準誤」——與其他地方同一條紀律：
            // 不要宣告一個抽樣噪音就能解釋的差異。
            double se = Math.Sqrt(DesignEffect) *
                        Math.Sqrt(Var(earlyRows) / Math.Max(1, earlyRows.Count) +
                                  Var(recentRows) / Math.Max(1, recentRows.Count));
            if (diff <= 2 * se) continue;

            signals.Add(new DriftSignal(agent, Math.Round(early, 3), Math.Round(recent, 3), r.Length,
                $"{agent}：早期 {earlyRows.Count} 筆 Brier {early:F3} → 近期 {recentRows.Count} 筆 {recent:F3}" +
                $"（惡化 {diff:F3}，2 個標準誤 {2 * se:F3}）"));
        }

        return signals.Count == 0
            ? DriftVerdict.None
            : new DriftVerdict(signals.ToImmutable(),
                "校準漂移：" + string.Join("；", signals.Select(x => x.Detail)) +
                " → 提高人類核准要求（漂移時學習訊號本身不可信，調權重只會學得更歪）");
    }

    private static double Brier(IEnumerable<(double P, bool Y)> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 ? 0 : list.Average(x => Math.Pow(x.P - (x.Y ? 1 : 0), 2));
    }

    /// <summary>單筆 Brier 分數的變異數——用來估「這個差值有多少是雜訊」。</summary>
    private static double Var(IReadOnlyList<(double P, bool Y)> rows)
    {
        if (rows.Count < 2) return 0.25;                       // 資料太少時給一個保守的大變異數
        var scores = rows.Select(x => Math.Pow(x.P - (x.Y ? 1 : 0), 2)).ToList();
        double m = scores.Average();
        return scores.Sum(x => (x - m) * (x - m)) / (scores.Count - 1);
    }
}

/// <summary>Mutation switch：永遠說沒漂移。</summary>
public sealed class SilentDriftAlarm : IDriftAlarm
{
    public DriftVerdict Check(PolicySnapshot policy) => new(ImmutableArray<DriftSignal>.Empty, "（壞的守門件）一律回報沒有漂移");
}
