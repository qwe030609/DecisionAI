// ============================================================================
//  Benchmark 用的腳本化世界：通用 solver / critic / designer 人格 + L3 模擬器 + L4 實驗。
//
//  重要限制（誠實說明）：LLM 是腳本，所以這份 benchmark 量的是「控制流程、守門件、
//  機率與決策引擎是否正確」，不是「模型答得好不好」。模型品質要用真模型加 replay 才能量。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

public static class ScriptedWorld
{
    /// <summary>三個廠商、兩個模型家族；角色與 AtsScenario 一致，但回應由案例資料生成。</summary>
    public static IEnumerable<(AgentSpec Spec, ILlm Llm)> Agents(BenchCase c)
    {
        yield return (Spec("solver-A", "vendor-x", "x-large", "solver", c.Domain, 1.0, 3.0), Llm("x-large", req => Respond(c, "A", req)));
        yield return (Spec("solver-B", "vendor-y", "y-large", "solver", "*", 0.8, 2.4), Llm("y-large", req => Respond(c, "B", req)));
        yield return (Spec("solver-C", "vendor-z", "z-medium", "solver", "*", 0.3, 0.9), Llm("z-medium", req => Respond(c, "C", req)));
        yield return (Spec("critic-D", "vendor-y", "y-small", "critic", "*", 0.2, 0.6), Llm("y-small", req => Respond(c, "D", req)));
        yield return (Spec("designer-E", "vendor-x", "x-medium", "experiment_designer", "*", 0.5, 1.5), Llm("x-medium", req => Respond(c, "E", req)));
    }

    private static AgentSpec Spec(string id, string vendor, string family, string role, string domain, double cin, double cout)
        => new(id, vendor, family, "2026Q1", ImmutableArray.Create(role),
               domain == "*" ? ImmutableArray.Create("*") : ImmutableArray.Create(domain, "*"), new CostProfile(cin, cout));

    private static ILlm Llm(string name, Func<LlmRequest, string> respond) => new ScriptedLlm(name, respond);

    // ── 回應生成 ──
    private static string Respond(BenchCase c, string who, LlmRequest req) => req.Role switch
    {
        "solver"              => SolverJson(c, who),
        "critic"              => CriticJson(ClaimIds(req.User)),
        "experiment_designer" => DesignerJson(ClaimIds(req.User)),
        Actors.StageSolver    => StageDeliverable(req.User),
        _ => "{}"
    };

    /// <summary>
    /// solver-A：依宣告順序與信心。solver-B：反序、信心打七五折（測主張合併與 ID 決定性）。
    /// solver-C：只提第一個 + 一個沒有任何引用的想法（測 L2 的「意見」規則）。
    /// </summary>
    private static string SolverJson(BenchCase c, string who)
    {
        var hyps = c.Hypotheses;
        if (hyps.IsEmpty) return """{"hypotheses":[]}""";

        var list = who switch
        {
            "A" => hyps.Select(h => (h, conf: h.Confidence)).ToList(),
            "B" => hyps.Reverse().Select(h => (h, conf: Math.Round(h.Confidence * 0.75, 2))).ToList(),
            _   => hyps.Take(1).Select(h => (h, conf: Math.Round(h.Confidence * 0.7, 2))).ToList()
        };

        var arr = new JsonArray();
        foreach (var (h, conf) in list)
            arr.Add(new JsonObject
            {
                ["statement"] = h.Statement,
                ["confidence"] = conf,
                ["evidenceFor"] = Ids(h.For),
                ["evidenceAgainst"] = Ids(h.Against)
            });

        if (who == "C" && hyps.Length >= 2)
            arr.Add(new JsonObject   // 一條證據都沒引用 → L2 會標記為意見，不進機率引擎
            {
                ["statement"] = "直覺上可能是環境或人員操作差異造成，但目前沒有資料支持",
                ["confidence"] = 0.25,
                ["evidenceFor"] = new JsonArray(),
                ["evidenceAgainst"] = new JsonArray()
            });

        return new JsonObject { ["hypotheses"] = arr }.ToJsonString();
    }

    private static JsonArray Ids(int[] idx)
    {
        var a = new JsonArray();
        foreach (var i in idx) a.Add($"EV-{i:000}");
        return a;
    }

    /// <summary>critic 只批判 prompt 裡真的存在的主張；第一個給低嚴重度，其餘遞增。</summary>
    private static string CriticJson(IReadOnlyList<string> claimIds)
    {
        var arr = new JsonArray();
        for (int i = 0; i < claimIds.Count; i++)
            arr.Add(new JsonObject
            {
                ["claimId"] = claimIds[i],
                ["issue"] = i == 0 ? "證據鏈完整，但需確認量測條件是否覆蓋全部情境"
                                   : $"與 {claimIds[0]} 相容，區分力不足；需要能分開兩者的觀測",
                ["severity"] = i == 0 ? 0.2 : Math.Min(0.9, 0.45 + 0.15 * i)
            });
        return new JsonObject { ["critiques"] = arr }.ToJsonString();
    }

    /// <summary>
    /// 設計者「賭」實驗會區分第一個假設：H1 在 outcome[0] 有 0.90，其餘 0.35。
    /// 若真相不是 H1，模擬器會回 outcome[1]，後驗就會離開 H1 —— 設計者會被 Evaluation 判錯。
    /// </summary>
    private static string DesignerJson(IReadOnlyList<string> claimIds)
    {
        if (claimIds.Count == 0) return """{"experiments":[]}""";
        var lik = new JsonObject();
        for (int i = 0; i < claimIds.Count; i++)
            lik[claimIds[i]] = i == 0 ? new JsonArray(0.90, 0.10) : new JsonArray(0.35, 0.65);

        return new JsonObject
        {
            ["experiments"] = new JsonArray(new JsonObject
            {
                ["description"] = "以受控條件重現並注入修復，比對修復前後的失效率",
                ["outcomes"] = new JsonArray("失效率顯著下降", "無明顯變化"),
                ["likelihoods"] = lik
            })
        }.ToJsonString();
    }

    private static string StageDeliverable(string user)
    {
        string task = Between(user, "本段任務：", "\n"), contract = Between(user, "輸出契約：", "\n");
        return $"【交付物】{task}\n依契約「{contract}」產出：\n1) 介面定義與單位\n2) 邊界條件與例外\n3) 驗收方式與判定門檻";
    }

    /// <summary>從 prompt 的「目前假設」區塊解析主張 ID：critic 不會憑空引用不存在的主張。</summary>
    private static IReadOnlyList<string> ClaimIds(string user)
    {
        var ids = new List<string>();
        foreach (var line in user.Split('\n'))
        {
            var t = line.TrimStart();
            int colon = t.IndexOf(':');
            if (colon is > 0 and < 5 && t[0] == 'H' && int.TryParse(t[1..colon], out _)) ids.Add(t[..colon]);
        }
        return ids;
    }

    private static string Between(string s, string start, string end)
    {
        int i = s.IndexOf(start, StringComparison.Ordinal); if (i < 0) return "";
        i += start.Length;
        int j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }

    // ── L3 模擬器：通過隱藏真相那一條，其餘不通過 ──
    public static Func<Claim, CaseState, (bool, double, string)?> TestVerifier(BenchCase c) => (claim, _) =>
    {
        if (claim.Kind != ClaimKind.Hypothesis || claim.IsOpinion) return null;
        int idx = HypIndex(c, claim.Statement);
        if (idx == 0) return null;                                    // 不在宣告清單內 → 測試不適用
        bool pass = idx == c.TrueHypothesis;
        return pass
            ? (true, 0.9, "可執行測試：注入修復後於受控條件下重現失效歸零，回退後重現 17/5000")
            : (false, 0.2, "可執行測試：在受控條件下無法重現此機制，量測結果與預期型態不符");
    };

    // ── L4 實驗：真相是 H1 → 觀察到 outcome[0]；否則 outcome[1] ──
    public static Func<Experiment, CaseState, CancellationToken, Task<int>> ExperimentRunner(BenchCase c)
        => (_, _, _) => Task.FromResult(c.TrueHypothesis == 1 ? 0 : 1);

    private static int HypIndex(BenchCase c, string statement)
    {
        for (int i = 0; i < c.Hypotheses.Length; i++)
            if (Claim.Jaccard(Claim.Tokens(statement), Claim.Tokens(c.Hypotheses[i].Statement)) >= 0.6) return i + 1;
        return 0;
    }
}
