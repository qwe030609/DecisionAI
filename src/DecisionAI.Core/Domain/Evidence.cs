// ============================================================================
//  證據 ≠ 意見。可靠度由來源類型查表（確定性），不是 LLM 逐筆估計。
// ============================================================================

namespace DecisionAI.Core.Domain;

public enum EvidenceSource { UserInput, SourceCode, RuntimeLog, Database, Web, Paper, Sensor, ExperimentResult, HistoricalOutcome }

/// <summary>尚未進 store 的證據草稿：沒有 ID、沒有時間戳。</summary>
public sealed record EvidenceDraft(EvidenceSource Source, string Origin, string Content);

public sealed record Evidence(string Id, EvidenceSource Source, string Origin, string Content, DateTime Timestamp)
{
    public double Reliability => ReliabilityOf(Source);

    public static double ReliabilityOf(EvidenceSource s) => s switch
    {
        EvidenceSource.ExperimentResult  => 0.98,
        EvidenceSource.HistoricalOutcome => 0.98,
        EvidenceSource.RuntimeLog        => 0.95,
        EvidenceSource.Sensor            => 0.95,
        EvidenceSource.SourceCode        => 0.95,
        EvidenceSource.Database          => 0.90,
        EvidenceSource.UserInput         => 0.80,
        EvidenceSource.Paper             => 0.75,
        EvidenceSource.Web               => 0.55,
        _ => 0.50
    };
}
