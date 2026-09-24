// ============================================================================
//  Conformal 覆蓋層（Rev2 §4.10 / Phase 2）——確定性區，不得出現 ILlm。
//
//  只對 TaskFamily 白名單啟用。反身與漂移題型硬性禁止：那些題目的可交換性
//  （exchangeability）被破壞，conformal 的覆蓋保證根本不成立，硬給就是假保證。
//
//  校準集切成兩份且不可混用：
//    · 校準用 —— 可以用主動學習挑樣本
//    · 稽核用 —— 必須隨機抽樣，否則主動學習會破壞可交換性，覆蓋率就量不準了
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Policy;

namespace DecisionAI.Modules.Assurance;

/// <summary>題型家族。白名單之外一律不啟用覆蓋層。</summary>
public static class TaskFamilies
{
    public const string Diagnosis = "diagnosis";                  // 類別 1：有可執行 verifier 的根因分析
    public const string Classification = "classification";        // 類別 4：邊界明確的分類
    public const string InformationProcessing = "information_processing";  // 類別 3c：資訊整理
    public const string Reflexive = "reflexive";                  // 反身 → 硬性禁止
    public const string Drifting = "drifting";                    // 漂移 → 硬性禁止

    public static readonly ImmutableHashSet<string> Whitelist =
        ImmutableHashSet.Create(Diagnosis, Classification, InformationProcessing);
}

/// <summary>一筆校準樣本：當時的信念分布與事後已知的真值。</summary>
public sealed record ConformalSample(string TaskFamily, ImmutableDictionary<string, double> Beliefs, string TrueClaimId, bool IsAudit)
{
    /// <summary>非一致性分數：真值被賦予的機率越低，分數越高。</summary>
    public double Nonconformity => 1 - Beliefs.GetValueOrDefault(TrueClaimId, 0);
}

public interface IConformalCalibrator
{
    bool IsApplicable(string taskFamily);
    CoverageLayer? Predict(IReadOnlyDictionary<string, double> beliefs, string taskFamily, PolicySnapshot policy);
}

public sealed class ConformalCalibrator : IConformalCalibrator
{
    /// <summary>低於這個校準樣本數就不給覆蓋層——樣本不足的保證是假保證。</summary>
    public int MinCalibration { get; init; } = 10;

    public bool IsApplicable(string taskFamily) => TaskFamilies.Whitelist.Contains(taskFamily);

    public CoverageLayer? Predict(IReadOnlyDictionary<string, double> beliefs, string taskFamily, PolicySnapshot policy)
    {
        if (!IsApplicable(taskFamily) || beliefs.Count == 0) return null;
        if (!policy.Conformal.TryGetValue(taskFamily, out var q) || q.CalibrationSize < MinCalibration) return null;

        // 預測集合 = { h : 非一致性分數 ≤ 門檻 } = { h : P(h) ≥ 1 − 門檻 }
        double keepAbove = 1 - q.Threshold;
        var set = beliefs.Where(kv => kv.Value >= keepAbove)
                         .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => kv.Key).ToImmutableArray();

        // 空集合沒有意義：至少放進最高機率的那一個，並讓覆蓋率誠實反映
        if (set.Length == 0)
            set = ImmutableArray.Create(beliefs.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key);

        return new CoverageLayer(1 - q.Alpha, set);
    }
}

/// <summary>Mutation switch：對任何題型都宣稱有覆蓋保證，包括反身題。</summary>
public sealed class AlwaysApplicableConformal : IConformalCalibrator
{
    public bool IsApplicable(string taskFamily) => true;
    public CoverageLayer? Predict(IReadOnlyDictionary<string, double> beliefs, string taskFamily, PolicySnapshot policy)
        => new(0.90, beliefs.Keys.Take(1).ToImmutableArray());
}

/// <summary>離線建立分位數。主動學習挑的樣本只能進校準集，稽核集永遠隨機。</summary>
public static class ConformalCalibrationBuilder
{
    public static ConformalQuantile Build(IEnumerable<ConformalSample> samples, double alpha = 0.10)
    {
        var all = samples.ToList();
        var calib = all.Where(s => !s.IsAudit).Select(s => s.Nonconformity).OrderBy(x => x).ToList();
        int audit = all.Count(s => s.IsAudit);
        if (calib.Count == 0) return new ConformalQuantile(alpha, 1.0, 0, audit);

        // 有限樣本修正的 (1−α) 分位數
        int k = (int)Math.Ceiling((calib.Count + 1) * (1 - alpha)) - 1;
        double threshold = calib[Math.Clamp(k, 0, calib.Count - 1)];
        return new ConformalQuantile(alpha, threshold, calib.Count, audit);
    }

    /// <summary>用稽核集實測覆蓋率：真值有沒有落在預測集合裡。這是唯一能驗證保證的方式。</summary>
    public static double EmpiricalCoverage(IEnumerable<ConformalSample> auditSet, ConformalQuantile q)
    {
        var audit = auditSet.Where(s => s.IsAudit).ToList();
        if (audit.Count == 0) return double.NaN;
        return audit.Count(s => s.Nonconformity <= q.Threshold) / (double)audit.Count;
    }
}

/// <summary>
/// 題型家族由「事實」決定，不是 LLM 說了算：反身旗標與 ground-truth 狀態都是請求者宣告的事實。
/// 判定順序刻意是「先排除，再歸類」——白名單是最後才給的。
/// </summary>
public static class TaskFamilyClassifier
{
    public static string Classify(DecisionAI.Core.Domain.ProblemFacts? facts)
    {
        if (facts is null) return TaskFamilies.Drifting;
        if (facts.Reflexive) return TaskFamilies.Reflexive;                       // 預測會改變被預測的系統
        if (facts.GroundTruth != DecisionAI.Core.Domain.GroundTruthStatus.Agreed) return TaskFamilies.Drifting;
        if (facts.BestAvailableVerifier >= DecisionAI.Core.Domain.VerifierLevel.L3_ExecutableTest) return TaskFamilies.Diagnosis;
        if (facts.BestAvailableVerifier >= DecisionAI.Core.Domain.VerifierLevel.L2_Rule) return TaskFamilies.Classification;
        return TaskFamilies.InformationProcessing;
    }
}
