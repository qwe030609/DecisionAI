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

    /// <summary>L3：對假設跑「可執行測試」——這裡是模擬器：只有 timer race 假設能被注入修復後重現/消失。</summary>
    public (bool Pass, double Score, string Note)? TestHypothesis(Claim claim, CaseState _)
    {
        if (claim.Kind != ClaimKind.Hypothesis) return null;
        string s = claim.Statement;
        if (s.Contains("KeepAlive") && (s.Contains("競爭") || s.Contains("race")))
            return (true, 0.9, "模擬器：對 KeepAliveTick 加鎖後 5000 次循環 0 次斷線；不加鎖重現 17 次");
        if (s.Contains("生命週期") || s.Contains("回收"))
            return (false, 0.2, "模擬器：追蹤 channel 回收時序，斷線時 channel 仍存活");
        if (s.Contains("網路") || s.Contains("交換機"))
            return (false, 0.1, "模擬器：注入網路瞬斷後斷線型態（PLC 側先斷）與現場不符");
        return null;   // 其他假設：測試不適用
    }

    public int RunExperiment(Experiment exp)
    {
        if (exp.Description.Contains("停用 KeepAlive")) return 0;   // 失效率大幅下降
        if (exp.Description.Contains("thread"))         return 0;   // 觀察到重疊
        return 1;                                                   // 其他實驗：無變化
    }

    public (int Cycles, int Failures) Deploy(string actionId, int cycles)
    {
        double rate = actionId switch { "A" => 0.0, "B" => 0.00002, "C" => 0.004, _ => 0.0033 };
        int failures = 0;
        for (int i = 0; i < cycles; i++) if (_rng.NextDouble() < rate) failures++;
        return (cycles, failures);
    }
}
