// ============================================================================
//  流程是資料（JSON），不是 prompt。
//  Timeout 的處置是明確的政策：FailClosed（預設）或 DegradeWithPartial。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DecisionAI.Core.Domain;

public enum TimeoutAction { FailClosed, DegradeWithPartial }

public sealed record TimeoutPolicy(int Seconds = 60, TimeoutAction OnTimeout = TimeoutAction.FailClosed);

public sealed record StepDef(
    string Id,
    string Type,
    string[]? DependsOn = null,
    Dictionary<string, string>? Params = null,
    int MaxRetries = 1,
    TimeoutPolicy? Timeout = null)
{
    public string[] Deps => DependsOn ?? Array.Empty<string>();
    public TimeoutPolicy TimeoutOrDefault => Timeout ?? new TimeoutPolicy();
    public string P(string key, string fallback = "") => Params?.GetValueOrDefault(key) ?? fallback;
    public int PInt(string key, int fallback) => int.TryParse(P(key), out var v) ? v : fallback;
}

public sealed record WorkflowDefinition(string Name, StepDef[] Steps)
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkflowDefinition FromJson(string json) => JsonSerializer.Deserialize<WorkflowDefinition>(json, Opts)!;

    /// <summary>設計時檢查：ID 唯一、依賴存在、無環。</summary>
    public void Validate()
    {
        var ids = Steps.Select(s => s.Id).ToList();
        if (ids.Distinct().Count() != ids.Count) throw new WorkflowRejectedException($"workflow {Name}: step id 重複");
        foreach (var s in Steps)
            foreach (var d in s.Deps)
                if (!ids.Contains(d)) throw new WorkflowRejectedException($"workflow {Name}: {s.Id} 依賴不存在的 {d}");

        var done = new HashSet<string>(); var remaining = Steps.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(s => s.Deps.All(done.Contains)).ToList();
            if (ready.Count == 0) throw new WorkflowRejectedException($"workflow {Name}: 有環");
            foreach (var r in ready) { done.Add(r.Id); remaining.Remove(r); }
        }
    }

    public ImmutableArray<string> StepIds => Steps.Select(s => s.Id).ToImmutableArray();
}

/// <summary>Fail-closed：整個 DAG 被拒絕執行（不是跳過某一步）。</summary>
public sealed class WorkflowRejectedException : Exception
{
    public WorkflowRejectedException(string message) : base(message) { }
}
