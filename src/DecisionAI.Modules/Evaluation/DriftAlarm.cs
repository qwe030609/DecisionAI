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
    /// <summary>樣本少於這個數就不告警：小樣本的「惡化」多半是雜訊。</summary>
    public int MinSamples { get; init; } = 12;

    /// <summary>近期 Brier 比早期差超過這個量才算漂移（Brier 越小越好）。</summary>
    public double Worsening { get; init; } = 0.08;

    public DriftVerdict Check(PolicySnapshot policy)
    {
        var signals = ImmutableArray.CreateBuilder<DriftSignal>();
        foreach (var (agent, curve) in policy.Calibration.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var r = curve.Records;
            if (r.Length < MinSamples) continue;

            int half = r.Length / 2;
            double early = Brier(r.Take(half));
            double recent = Brier(r.Skip(half));
            if (recent - early <= Worsening) continue;

            signals.Add(new DriftSignal(agent, Math.Round(early, 3), Math.Round(recent, 3), r.Length,
                $"{agent}：早期 {r.Length - half} 筆 Brier {early:F3} → 近期 {half} 筆 {recent:F3}（惡化 {recent - early:F3}）"));
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
}

/// <summary>Mutation switch：永遠說沒漂移。</summary>
public sealed class SilentDriftAlarm : IDriftAlarm
{
    public DriftVerdict Check(PolicySnapshot policy) => new(ImmutableArray<DriftSignal>.Empty, "（壞的守門件）一律回報沒有漂移");
}
