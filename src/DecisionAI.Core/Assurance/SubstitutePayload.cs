// ============================================================================
//  型別化替代交付（Rev2 §4.10 / Q5）
//  拒絕的是「帶信心值的方向性答案」這種特定形式的斷言，不是拒絕提供幫助。
//  不同 ReasonCode 該附什麼，由型別系統強制：ReflexiveDirection 產不出 ConsiderationMap。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Assurance;

public enum ReasonCode
{
    None,
    NoVerifier,                 // Triage 第一問：沒有 L3+ 的 verifier
    NoGroundTruth,              // Triage 第二問：沒有各方同意的 ground truth
    DefinitionalDispute,        // Triage 第三問：爭議在定義本身
    ReflexiveDirection,         // 反身系統 → 禁止方向預測
    DelegatedToExternalModel,   // Tool/Model Router 轉介專業數值模型
    InsufficientCoverage,       // 覆蓋層不足（Phase 2 conformal）
    PipelineHalted,             // 邊界攔截 / 人類拒絕 → 流程中止
    NoAdmissibleAction          // 所有行動都超過可接受損失 → 交人
}

public abstract record SubstitutePayload
{
    /// <summary>這個 payload 允許掛在哪些 ReasonCode 上。型別綁定寫死在編譯期。</summary>
    public abstract ImmutableArray<ReasonCode> ValidFor { get; }
    public abstract string Kind { get; }
    /// <summary>payload 內所有宣稱事實的證據引用：L2 會逐一檢查是否存在。</summary>
    public abstract ImmutableArray<string> EvidenceRefs { get; }
}

// ── 事實與情境（反身系統）──────────────────────────────────────────
public sealed record FactLine(string Text, ImmutableArray<string> EvidenceFor);

/// <summary>領先指標必須是可觀察的：有資料來源、有閾值、有觀察頻率，否則就是「密切關注市場動向」這類廢話。</summary>
public sealed record LeadingIndicator(string Name, string Source, string Threshold, string Frequency)
{
    public bool IsObservable => Source.Trim().Length > 0 && Threshold.Trim().Length > 0 && Frequency.Trim().Length > 0;
}

public sealed record Scenario(string Name, ImmutableArray<FactLine> Basis, ImmutableArray<LeadingIndicator> Indicators);

public sealed record RiskExit(string Exposure, string ExitCondition);

/// <summary>ReflexiveDirection 專用。絕不可附：任何方向暗示、機率數字。</summary>
public sealed record ScenarioBriefing(
    ImmutableArray<FactLine> Facts, ImmutableArray<Scenario> Scenarios, RiskExit Risk) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor => ImmutableArray.Create(ReasonCode.ReflexiveDirection);
    public override string Kind => nameof(ScenarioBriefing);
    public override ImmutableArray<string> EvidenceRefs =>
        Facts.SelectMany(f => f.EvidenceFor)
             .Concat(Scenarios.SelectMany(s => s.Basis).SelectMany(f => f.EvidenceFor))
             .Distinct().ToImmutableArray();
}

// ── 考量地圖（無 ground truth）────────────────────────────────────
public sealed record Consideration(string Name, string WhoItMattersTo, ImmutableArray<string> EvidenceFor);
public sealed record Conflict(string Between, string Nature);
public sealed record OpenQuestion(string Question, string WhoDecides);

/// <summary>NoGroundTruth 專用。絕不可附：結論、偽客觀評分。</summary>
public sealed record ConsiderationMap(
    ImmutableArray<Consideration> Items, ImmutableArray<Conflict> Conflicts,
    ImmutableArray<OpenQuestion> ForHumans) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor => ImmutableArray.Create(ReasonCode.NoGroundTruth);
    public override string Kind => nameof(ConsiderationMap);
    public override ImmutableArray<string> EvidenceRefs => Items.SelectMany(i => i.EvidenceFor).Distinct().ToImmutableArray();
}

// ── 定義地圖（定義爭議）───────────────────────────────────────────
public sealed record Definition(string Name, string Statement, string Consequence, ImmutableArray<string> EvidenceFor);

/// <summary>DefinitionalDispute 專用。絕不可附：各家立場的「傾向」、任何信心值。</summary>
public sealed record DefinitionMap(
    ImmutableArray<Definition> Definitions, string WhyUndecidable) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor => ImmutableArray.Create(ReasonCode.DefinitionalDispute);
    public override string Kind => nameof(DefinitionMap);
    public override ImmutableArray<string> EvidenceRefs => Definitions.SelectMany(d => d.EvidenceFor).Distinct().ToImmutableArray();
}

// ── 轉介（專業數值模型）──────────────────────────────────────────
/// <summary>DelegatedToExternalModel 專用。絕不可附：自己編的數值。</summary>
public sealed record DelegationHandoff(
    string ToolId, string WhyThisTool, string HowToRead, string UncertaintyNotes,
    ImmutableArray<string> InputsRequired) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor => ImmutableArray.Create(ReasonCode.DelegatedToExternalModel);
    public override string Kind => nameof(DelegationHandoff);
    public override ImmutableArray<string> EvidenceRefs => ImmutableArray<string>.Empty;
}

// ── 無 verifier / 流程中止 / 無可採行動 ─────────────────────────────
public sealed record MissingCapability(string What, string WhyItWouldHelp);

/// <summary>NoVerifier、PipelineHalted、NoAdmissibleAction 共用：說清楚少了什麼、補上什麼就能重新提交。</summary>
public sealed record BlockedHandback(
    string WhatIsMissing, ImmutableArray<MissingCapability> Needed, string HowToResubmit) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor =>
        ImmutableArray.Create(ReasonCode.NoVerifier, ReasonCode.PipelineHalted, ReasonCode.NoAdmissibleAction);
    public override string Kind => nameof(BlockedHandback);
    public override ImmutableArray<string> EvidenceRefs => ImmutableArray<string>.Empty;
}

/// <summary>InsufficientCoverage 專用（Phase 2 conformal 啟用後才會產生）。</summary>
public sealed record WidenedPredictionSet(
    ImmutableArray<string> Set, double Alpha, ImmutableArray<string> MissingEvidence) : SubstitutePayload
{
    public override ImmutableArray<ReasonCode> ValidFor => ImmutableArray.Create(ReasonCode.InsufficientCoverage);
    public override string Kind => nameof(WidenedPredictionSet);
    public override ImmutableArray<string> EvidenceRefs => ImmutableArray<string>.Empty;
}
