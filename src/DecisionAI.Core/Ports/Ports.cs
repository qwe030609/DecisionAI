// ============================================================================
//  Ports — 所有非決定性（時間、亂數、LLM、人、工具）都走介面。
//  這不是風格：可重放與 mutation testing 都以此為前提。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Policy;

namespace DecisionAI.Core.Ports;

public interface IClock { DateTime UtcNow { get; } }

public interface IRandomSource
{
    double NextDouble();
    int Next(int max);
    /// <summary>獨立且可重現的子流：新增一個隨機呼叫不會打掉既有 golden。</summary>
    IRandomSource Fork(string label);
}

// ── LLM ──
/// <summary>把「同一個 LLM 呼叫」定義成可定址的：錄製 / 重放 / 腳本都靠這把 key。</summary>
public readonly record struct LlmCallKey(string CaseId, string StepId, string AgentId, int CallIndex);

public sealed record LlmRequest(LlmCallKey Key, string Role, string System, string User, double Temperature);

public interface ILlm
{
    string Name { get; }
    Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default);
}

// ── 驗證 ──
/// <summary>
/// 驗證器讀狀態、回傳事件，不直接寫 Journal：這樣平行的 L1 ∥ L2 才能由引擎依宣告順序序列化寫入（可重放）。
/// </summary>
public interface IVerifier
{
    string Id { get; }
    VerifierLevel Level { get; }
    Task<IReadOnlyList<CaseEvent>> VerifyAsync(CaseState state, string stepId, CancellationToken ct = default);
}

public sealed record VerifierInventory(ImmutableArray<VerifierLevel> Levels)
{
    public VerifierLevel Best => Levels.IsDefaultOrEmpty ? VerifierLevel.None : Levels.Max();
    public bool HasRealVerifier => VerifierTrust.CountsAsVerifier(Best);
}

// ── 人 ──
public enum HumanRole { Approver, Rater, Arbiter, UtilityOwner, Accountable }

public sealed record HumanRequest(string CaseId, string StepId, HumanRole Role, string What, ImmutableArray<string> Context);
public sealed record HumanVerdict(bool Approved, string Rationale);

public interface IHumanGateway
{
    Task<HumanVerdict> RequestAsync(HumanRequest req, CancellationToken ct = default);
}

// ── 證據 ──
public interface IEvidenceStore
{
    Evidence Admit(EvidenceDraft draft);
    Evidence? Get(string id);
    bool Exists(string id);
}

public sealed record EvidenceRenderResult(string Text, ImmutableArray<string> FlaggedEvidenceIds);

/// <summary>證據進 prompt 前一律包在中性化區塊並掃描注入；證據永遠不進 system prompt。</summary>
public interface IInjectionGuard
{
    EvidenceRenderResult Render(IEnumerable<Evidence> evidence);
}

// ── 學習狀態 ──
public interface IPolicyStore
{
    PolicySnapshot Current { get; }
    PolicySnapshot Pin(long? version = null);
    PolicySnapshot Commit(PolicyDelta delta);
}
