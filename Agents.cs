// ============================================================================
//  Agents — Registry + Runner
//  Registry 存的不是「哪個模型最強」，是 P(success | agent, domain, role) 的 Beta 後驗，
//  以及 agent 之間的錯誤相關性。冷啟動時所有人一樣（Beta(1,1)），選擇靠多樣性。
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DecisionAI.Domain;

namespace DecisionAI.Agents;

public sealed record AgentSpec(
    string AgentId,
    string Provider,
    ILlm Model,
    string[] Roles,
    string[] Domains,          // "*" = 通用
    string[] Tools,
    double CostInPer1k,
    double CostOutPer1k);

/// <summary>Beta(a, b) 後驗。先驗 Beta(1,1)：樣本少時自然收縮到 0.5，3 個案例的 0.91 不會被當真。</summary>
public sealed class AgentStats
{
    public double A { get; private set; } = 1;
    public double B { get; private set; } = 1;
    public int N => (int)(A + B - 2);
    public double Mean => A / (A + B);
    public void Observe(bool success) { if (success) A += 1; else B += 1; }
}

public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentSpec> _agents = new();
    private readonly Dictionary<(string Agent, string Domain, string Role), AgentStats> _stats = new();
    private readonly Dictionary<(string CaseId, string Agent), bool> _wrong = new();   // 錯誤指標，算相關性用
    private readonly object _lock = new();

    public IEnumerable<AgentSpec> All => _agents.Values;
    public AgentSpec Get(string id) => _agents[id];

    public void Register(AgentSpec a) => _agents[a.AgentId] = a;

    public AgentStats Stats(string agent, string domain, string role)
    {
        lock (_lock)
        {
            var key = (agent, domain, role);
            if (!_stats.TryGetValue(key, out var s)) _stats[key] = s = new AgentStats();
            return s;
        }
    }

    /// <summary>錯誤相關性（phi 係數）：兩個 agent 在共同案例上是否傾向一起錯。共同案例少於 3 個 → 視為 0。</summary>
    public double ErrorCorrelation(string a, string b)
    {
        lock (_lock)
        {
            var shared = _wrong.Keys.Where(k => k.Agent == a).Select(k => k.CaseId)
                .Intersect(_wrong.Keys.Where(k => k.Agent == b).Select(k => k.CaseId)).ToList();
            if (shared.Count < 3) return 0;
            int n11 = 0, n10 = 0, n01 = 0, n00 = 0;
            foreach (var c in shared)
            {
                bool wa = _wrong[(c, a)], wb = _wrong[(c, b)];
                if (wa && wb) n11++; else if (wa) n10++; else if (wb) n01++; else n00++;
            }
            double den = Math.Sqrt((double)(n11 + n10) * (n01 + n00) * (n11 + n01) * (n10 + n00));
            return den == 0 ? 0 : ((double)n11 * n00 - (double)n10 * n01) / den;
        }
    }

    /// <summary>
    /// 選 k 個 agent：分數 = Beta 後驗均值；貪婪加入時扣掉「與已選者的錯誤相關性」與「同廠商」懲罰。
    /// exclude = 這個 Case 裡不該再出現的人（例如：評審不能是本案的 solver）。
    /// </summary>
    public IReadOnlyList<AgentSpec> Select(string role, string domain, int k, IEnumerable<string>? exclude = null,
                                           double corrPenalty = 0.3, double sameProviderPenalty = 0.05)
    {
        var banned = new HashSet<string>(exclude ?? Array.Empty<string>());
        var pool = _agents.Values
            .Where(a => a.Roles.Contains(role) && !banned.Contains(a.AgentId))
            .Where(a => domain == "*" || a.Domains.Contains(domain) || a.Domains.Contains("*"))
            .ToList();

        var chosen = new List<AgentSpec>();
        while (chosen.Count < k && pool.Count > 0)
        {
            AgentSpec best = pool[0]; double bestScore = double.NegativeInfinity;
            foreach (var a in pool)
            {
                double s = Stats(a.AgentId, domain, role).Mean
                         + (a.Domains.Contains(domain) ? 0.02 : 0)                                  // 專精略優先
                         - corrPenalty * chosen.Select(c => Math.Max(0, ErrorCorrelation(a.AgentId, c.AgentId))).DefaultIfEmpty(0).Max()
                         - sameProviderPenalty * chosen.Count(c => c.Provider == a.Provider);      // 異質優先
                if (s > bestScore) { bestScore = s; best = a; }
            }
            chosen.Add(best); pool.Remove(best);
        }
        return chosen;
    }

    /// <summary>只有 L3+ 的結果更新權重；錯誤指標一律記錄（相關性分析用）。</summary>
    public void RecordResult(string caseId, string agent, string domain, string role, bool success, VerifierLevel level)
    {
        lock (_lock)
        {
            if (VerifierTrust.UpdatesWeights(level)) Stats(agent, domain, role).Observe(success);
            _wrong[(caseId, agent)] = !success;
        }
    }

    public string Report()
    {
        lock (_lock)
        {
            var lines = new List<string> { "agent            domain/role                      mean   n" };
            foreach (var kv in _stats.OrderBy(k => k.Key.Agent).ThenBy(k => k.Key.Role))
                lines.Add($"{kv.Key.Agent,-16} {kv.Key.Domain + "/" + kv.Key.Role,-32} {kv.Value.Mean:F2}   {kv.Value.N}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>執行一個 agent：計時、估成本、記錄到 Case。成本粗估 4 chars/token。</summary>
public sealed class AgentRunner
{
    public async Task<AgentRun> RunAsync(AgentSpec agent, string role, string system, string user, DecisionCase c,
                                         double temperature = 0.5, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        AgentRun run;
        try
        {
            string raw  = await agent.Model.CompleteAsync(new LlmRequest(role, system, user, temperature), ct);
            double cost = (system.Length + user.Length) / 4.0 / 1000 * agent.CostInPer1k + raw.Length / 4.0 / 1000 * agent.CostOutPer1k;
            run = new AgentRun(agent.AgentId, role, raw, cost, sw.Elapsed, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run = new AgentRun(agent.AgentId, role, "", 0, sw.Elapsed, false, ex.Message);
        }
        lock (c.AgentRuns) { c.AgentRuns.Add(run); c.CostSpent += run.Cost; }
        return run;
    }
}

/// <summary>角色提示：一律要求 JSON，這樣輸出才能被確定性程式解析、驗證、引用。</summary>
public static class RolePrompts
{
    public const string Solver =
        "你是解題者。根據問題與證據提出假設。只輸出 JSON：" +
        "{\"hypotheses\":[{\"statement\":\"...\",\"confidence\":0.0-1.0,\"evidenceFor\":[\"EV-..\"],\"evidenceAgainst\":[\"EV-..\"]}]}。" +
        "每個假設必須引用證據 ID；沒有證據支持的想法請標 evidenceFor 為空陣列。";

    public const string Critic =
        "你是批判者，不是解題者。逐一檢查每個主張的證據是否支持、有無替代解釋。只輸出 JSON：" +
        "{\"critiques\":[{\"claimId\":\"H1\",\"issue\":\"...\",\"severity\":0.0-1.0}]}";

    public const string ExperimentDesigner =
        "你是實驗設計者。設計能區分各假設的實驗，並在跑實驗之前登記每個假設下各結果的機率。只輸出 JSON：" +
        "{\"experiments\":[{\"description\":\"...\",\"outcomes\":[\"...\",\"...\"],\"likelihoods\":{\"H1\":[p1,p2],\"H2\":[p1,p2]}}]}";

    public const string Profiler =
        "把問題分類到以下領域之一，只輸出 JSON {\"domain\":\"...\"}：csharp_concurrency, distributed_system, " +
        "instrument_control, plc, finance, ethics, creative, general";

    public const string Analyst = "你是分析師。依指定視角輸出 3-5 條要點，不下方向結論。";
    public const string Synthesizer = "合併各視角成備忘：事實 / 情境與觀察指標 / 風險與退出條件。禁止方向結論。";
}

/// <summary>結構化輸出解析（容忍 ```json 圍欄）。解析失敗 → 空結果，由 L2 規則驗證器記錄。</summary>
public static class AgentOutputs
{
    public sealed record HypothesisOut(string Statement, double Confidence, string[] EvidenceFor, string[] EvidenceAgainst);
    public sealed record CritiqueOut(string ClaimId, string Issue, double Severity);
    public sealed record ExperimentOut(string Description, string[] Outcomes, Dictionary<string, double[]> Likelihoods);

    public static JsonNode? ParseJson(string raw)
    {
        int s = raw.IndexOf('{'), e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        try { return JsonNode.Parse(raw[s..(e + 1)]); } catch (JsonException) { return null; }
    }

    public static List<HypothesisOut> Hypotheses(string raw)
    {
        var arr = ParseJson(raw)?["hypotheses"]?.AsArray();
        if (arr is null) return new();
        return arr.Select(h => new HypothesisOut(
            (string?)h!["statement"] ?? "",
            Math.Clamp((double?)h["confidence"] ?? 0.5, 0, 1),
            Strings(h["evidenceFor"]), Strings(h["evidenceAgainst"]))).Where(h => h.Statement.Length > 0).ToList();
    }

    public static List<CritiqueOut> Critiques(string raw)
    {
        var arr = ParseJson(raw)?["critiques"]?.AsArray();
        if (arr is null) return new();
        return arr.Select(c => new CritiqueOut((string?)c!["claimId"] ?? "", (string?)c["issue"] ?? "",
            Math.Clamp((double?)c["severity"] ?? 0.5, 0, 1))).ToList();
    }

    public static List<ExperimentOut> Experiments(string raw)
    {
        var arr = ParseJson(raw)?["experiments"]?.AsArray();
        if (arr is null) return new();
        var list = new List<ExperimentOut>();
        foreach (var x in arr)
        {
            var lik = new Dictionary<string, double[]>();
            foreach (var kv in x!["likelihoods"]?.AsObject() ?? new JsonObject())
                lik[kv.Key] = kv.Value!.AsArray().Select(v => Math.Clamp((double?)v ?? 0.5, 0.01, 0.99)).ToArray();
            list.Add(new ExperimentOut((string?)x["description"] ?? "", Strings(x["outcomes"]), lik));
        }
        return list;
    }

    public static string? Domain(string raw) => (string?)ParseJson(raw)?["domain"];

    private static string[] Strings(JsonNode? n) => n?.AsArray().Select(v => (string?)v ?? "").Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>();
}
