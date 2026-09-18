// ============================================================================
//  ICaseJournal — 唯一寫入口。Apply 先過權限矩陣，再 Reduce。
//  單寫者：平行只發生在 I/O，所有狀態變更序列化通過這裡。
// ============================================================================

using DecisionAI.Core.Ports;

namespace DecisionAI.Core.Journal;

public sealed record ApplyResult(bool Accepted, string? Reason)
{
    public static readonly ApplyResult Ok = new(true, null);
    public static ApplyResult Rejected(string why) => new(false, why);
}

public interface ICaseJournal
{
    string CaseId { get; }
    CaseState State { get; }
    IReadOnlyList<CaseEvent> Events { get; }
    ApplyResult Apply(CaseEvent e);
}

/// <summary>權限矩陣：誰能寫哪種事件。由 reducer 執行，不靠 prompt 約定。</summary>
public interface IRolePermission
{
    bool CanWrite(string actorRole, CaseEvent e);
}

/// <summary>寫入被權限矩陣拒絕：這是安全事件，不是可忽略的錯誤。</summary>
public sealed class JournalWriteRejectedException : Exception
{
    public JournalWriteRejectedException(string message) : base(message) { }
}

public sealed class CaseJournal : ICaseJournal
{
    private readonly List<CaseEvent> _events = new();
    private readonly IRolePermission _permission;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public string CaseId { get; }
    public CaseState State { get; private set; }
    public IReadOnlyList<CaseEvent> Events { get { lock (_gate) return _events.ToArray(); } }

    public CaseJournal(string caseId, IRolePermission permission, IClock clock)
    {
        CaseId = caseId; _permission = permission; _clock = clock; State = CaseState.Empty(caseId);
    }

    public ApplyResult Apply(CaseEvent e)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(e.ActorRole) || string.IsNullOrWhiteSpace(e.StepId))
                return ApplyResult.Rejected($"{e.GetType().Name} 沒有標記 StepId / ActorRole → 拒絕（fail-closed）");
            if (!_permission.CanWrite(e.ActorRole, e))
                return ApplyResult.Rejected($"角色 {e.ActorRole}（{e.ActorId}）不得寫入 {e.GetType().Name}");
            var stamped = e with { CaseId = CaseId, Seq = _events.Count + 1, At = _clock.UtcNow };
            State = CaseReducer.Reduce(State, stamped);
            _events.Add(stamped);
            return ApplyResult.Ok;
        }
    }
}

public static class JournalExtensions
{
    /// <summary>被拒絕就丟例外：呼叫端若是 LLM 越權寫入，這裡就是攔截點。</summary>
    public static void ApplyOrThrow(this ICaseJournal j, CaseEvent e)
    {
        var r = j.Apply(e);
        if (!r.Accepted) throw new JournalWriteRejectedException(r.Reason!);
    }

    public static void ApplyAll(this ICaseJournal j, IEnumerable<CaseEvent> events)
    {
        foreach (var e in events) j.ApplyOrThrow(e);
    }
}
