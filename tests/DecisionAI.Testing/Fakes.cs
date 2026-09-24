// ============================================================================
//  測試基礎設施：可定址的假 LLM、固定時鐘、腳本化的人、模擬器。
//  這些不是測試本身；Host 的離線 demo 也用它們。
// ============================================================================

using System.Collections.Concurrent;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;

namespace DecisionAI.Testing;

/// <summary>每次讀取前進 1 秒：事件時間戳可重現。</summary>
public sealed class FixedClock : IClock
{
    private DateTime _now;
    private readonly object _lock = new();
    public FixedClock(DateTime? start = null) => _now = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow { get { lock (_lock) { _now = _now.AddSeconds(1); return _now; } } }
}

/// <summary>
/// 腳本化 LLM：先查 (CaseId, StepId, AgentId, CallIndex) 精確表，沒有就依角色回 fallback。
/// 完全決定性；記錄每次呼叫供測試斷言（例如「拒答時 LLM 呼叫次數必須是 0」）。
/// </summary>
public sealed class ScriptedLlm : ILlm
{
    private readonly Func<LlmRequest, string> _fallback;
    private readonly Dictionary<LlmCallKey, string> _exact = new();
    public string Name { get; }
    public ConcurrentQueue<LlmRequest> Calls { get; } = new();

    public ScriptedLlm(string name, Func<LlmRequest, string> fallback) { Name = name; _fallback = fallback; }

    public ScriptedLlm Script(LlmCallKey key, string response) { _exact[key] = response; return this; }

    public Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue(req);
        return Task.FromResult(_exact.TryGetValue(req.Key, out var r) ? r : _fallback(req));
    }
}

/// <summary>永遠不回應的 LLM：用來製造 timeout。</summary>
public sealed class HangingLlm : ILlm
{
    public string Name => "hanging";
    public async Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "";
    }
}

public sealed class ScriptedHumanGateway : IHumanGateway
{
    private readonly Func<HumanRequest, HumanVerdict> _decide;
    public List<HumanRequest> Requests { get; } = new();
    public ScriptedHumanGateway(Func<HumanRequest, HumanVerdict> decide) => _decide = decide;
    public static ScriptedHumanGateway AlwaysApprove() => new(_ => new HumanVerdict(true, "scripted: approve"));

    /// <summary>
    /// 秒殺型核准者：永遠同意、永遠一秒半就按下去。
    /// 這不是誇張的假設——它就是橡皮圖章化在資料上長的樣子，
    /// 而 AutomationBiasMonitor 要能認出它。
    /// </summary>
    public static ScriptedHumanGateway RubberStamp(double seconds = 1.5) =>
        new(_ => new HumanVerdict(true, "scripted: approve") { Seconds = seconds });

    public Task<HumanVerdict> RequestAsync(HumanRequest req, CancellationToken ct = default)
    {
        Requests.Add(req);
        return Task.FromResult(_decide(req));
    }
}

/// <summary>真實系統的替身。隱藏真相 = H1（timer race）。正式版：這就是你的 HIL / staging 環境。</summary>
public sealed class AtsSimulator
{
    private readonly Random _rng = new(2026);

    /// <summary>
    /// L3：對假設跑「可執行測試」。Rev2 之後判定依據是四元組的 Mechanism，
    /// 不再是自然語言關鍵字——這正是 ClaimFrame 帶來的好處。
    /// </summary>
    public (bool Pass, double Score, string Note)? TestHypothesis(Claim claim, CaseState _)
    {
        if (claim.Kind != ClaimKind.Hypothesis || claim.IsOpinion) return null;
        return claim.Frame.Mechanism switch
        {
            Mechanisms.RaceCondition =>
                (true, 0.9, "模擬器：對 KeepAliveTick 加鎖後 5000 次循環 0 次斷線；移除鎖重現 17 次"),
            Mechanisms.LifecycleMisuse =>
                (false, 0.2, "模擬器：追蹤 channel 回收時序，斷線瞬間 channel 仍存活"),
            Mechanisms.EnvironmentalStress =>
                (false, 0.1, "模擬器：注入鏈路異常後的斷線型態（對端先斷）與現場不符"),
            _ => null   // 其他機制：這個模擬器不適用
        };
    }

    public int RunExperiment(Experiment exp)
        => exp.Description.Contains("停用 KeepAlive") || exp.Description.Contains("thread") ? 0 : 1;

    public (int Cycles, int Failures) Deploy(string actionId, int cycles)
    {
        double rate = actionId switch { "A" => 0.0, "B" => 0.00002, "C" => 0.004, _ => 0.0033 };
        int failures = 0;
        for (int i = 0; i < cycles; i++) if (_rng.NextDouble() < rate) failures++;
        return (cycles, failures);
    }
}
