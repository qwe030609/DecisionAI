// ============================================================================
//  Benchmark 用的腳本化世界：通用 solver / critic / designer / briefer 人格
//  + L3 模擬器（依 Mechanism 判定）+ L4 實驗。
//
//  重要限制（誠實說明）：LLM 是腳本，所以這份 benchmark 量的是「控制流程、守門件、
//  機率與決策引擎是否正確」，不是「模型答得好不好」。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Catalog;
using DecisionAI.Testing;

namespace DecisionAI.Benchmark;

public static class ScriptedWorld
{
    /// <summary>三個廠商、五個模型家族；briefer 角色是 Rev2 新增（拒答時的替代交付）。</summary>
    public static IEnumerable<(AgentSpec Spec, ILlm Llm)> Agents(BenchCase c)
    {
        yield return (Spec("solver-A", "vendor-x", "x-large", new[] { "solver" }, c.Domain, 1.0, 3.0), Llm("x-large", r => Respond(c, "A", r)));
        yield return (Spec("solver-B", "vendor-y", "y-large", new[] { "solver" }, "*", 0.8, 2.4), Llm("y-large", r => Respond(c, "B", r)));
        yield return (Spec("solver-C", "vendor-z", "z-medium", new[] { "solver", "briefer" }, "*", 0.3, 0.9), Llm("z-medium", r => Respond(c, "C", r)));
        yield return (Spec("critic-D", "vendor-y", "y-small", new[] { "critic", "briefer" }, "*", 0.2, 0.6), Llm("y-small", r => Respond(c, "D", r)));
        yield return (Spec("designer-E", "vendor-x", "x-medium", new[] { "experiment_designer" }, "*", 0.5, 1.5), Llm("x-medium", r => Respond(c, "E", r)));
    }

    /// <summary>
    /// M8 用：briefer 產出「樂觀寫很多、悲觀寫很少」的不對稱情境。
    /// 這是真實模型很容易犯的錯，也是「拒答卻變相指方向」的典型樣子。
    /// </summary>
    public static IEnumerable<(AgentSpec Spec, ILlm Llm)> AgentsWithSkewedBriefer(BenchCase c)
        => Agents(c).Select(a => a.Spec.EligibleRoles.Contains("briefer")
            ? (a.Spec, (ILlm)new ScriptedLlm(a.Spec.AgentId,
                r => r.Role == "briefer" ? SkewedBrief(r.System) : Respond(c, "S", r)))
            : a);

    private static string SkewedBrief(string systemPrompt)
        => systemPrompt.Contains("不可做方向預測") ? SkewedBriefingJson : Brief(systemPrompt);

    /// <summary>樂觀情境 5 條證據、悲觀 1 條；篇幅也差一個量級 —— 讀者一定讀得出方向。</summary>
    private const string SkewedBriefingJson = """
        {"facts":[{"text":"以下為可查證的事實","evidenceFor":["EV-001"]}],
         "scenarios":[
           {"name":"情境 A","basis":[{"text":"這個方向有大量佐證：資料顯示趨勢延續、動能維持、結構性因素未變、外部條件亦有利，且近期觀察全數一致，綜合而言支持力道明顯偏強，各項指標同向。","evidenceFor":["EV-001","EV-002","EV-003","EV-001","EV-002"]}],
            "indicators":[{"name":"指標 A","source":"EV-002 的資料來源","threshold":"單週 > 10%","frequency":"每週"}]},
           {"name":"情境 B","basis":[{"text":"反向證據有限。","evidenceFor":["EV-001"]}],
            "indicators":[{"name":"指標 B","source":"EV-001 的資料來源","threshold":"連續 5 日","frequency":"每日"}]}],
         "risk":{"exposure":"部位受波動影響","exitCondition":"觸及最大可承受回撤"}}
        """;

    /// <summary>降級實驗用：把整個池壓成 n 個模型家族，其餘不變。</summary>
    public static IEnumerable<(AgentSpec Spec, ILlm Llm)> AgentsWithFamilies(BenchCase c, int families)
    {
        var all = Agents(c).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var (spec, llm) = all[i];
            yield return (spec with { BaseModelFamily = $"fam-{i % families}", Vendor = $"vendor-{i % families}" }, llm);
        }
    }

    private static AgentSpec Spec(string id, string vendor, string family, string[] roles, string domain, double cin, double cout)
        => new(id, vendor, family, "2026Q1", roles.ToImmutableArray(),
               domain == "*" ? ImmutableArray.Create("*") : ImmutableArray.Create(domain, "*"), new CostProfile(cin, cout));

    private static ILlm Llm(string name, Func<LlmRequest, string> respond) => new ScriptedLlm(name, respond);

    public static InMemoryClaimCatalog Catalog(BenchCase c)
        => InMemoryClaimCatalog.Seeded(c.CatalogSeed.Select(f => (c.Domain, f)).ToArray());

    // ── 回應生成 ──
    private static string Respond(BenchCase c, string who, LlmRequest req) => req.Role switch
    {
        "solver"              => SolverJson(c, who),
        "critic"              => CriticJson(ClaimIds(req.User)),
        "experiment_designer" => DesignerJson(ClaimIds(req.User)),
        "briefer"             => Brief(req.System),
        Actors.StageSolver    => StageDeliverable(req.User),
        _ => "{}"
    };

    /// <summary>
    /// solver-A：依宣告順序。solver-B：反序，且把 Trigger/Observable 換句話說
    /// （測四元組合併與 canonical id 的穩定性）。solver-C：只提第一個 + 一個沒有引用的想法。
    /// </summary>
    private static string SolverJson(BenchCase c, string who)
    {
        var hyps = c.Hypotheses;
        if (hyps.IsEmpty) return """{"hypotheses":[]}""";

        List<(ClaimFrame Frame, LikertBelief Conf, int[] For, int[] Against)> list = who switch
        {
            "A" => hyps.Select(h => (h.Frame, h.Confidence, h.For, h.Against)).ToList(),
            "B" => hyps.Reverse().Select(h => (
                     h.Frame with { Trigger = "（另一種說法）" + h.Frame.Trigger, Observable = h.Frame.Observable + "（換句話說）" },
                     Likert.Shift(h.Confidence, -1), h.For, h.Against)).ToList(),
            _   => hyps.Take(1).Select(h => (h.Frame, Likert.Shift(h.Confidence, -1), h.For, h.Against)).ToList()
        };

        var arr = new JsonArray();
        foreach (var (frame, conf, forIdx, against) in list)
            arr.Add(Node(frame, conf.ToString(), forIdx, against));

        if (who == "C" && hyps.Length >= 2)
            // 一條證據都沒引用 → L2 會標記為意見，不進機率引擎
            arr.Add(Node(new ClaimFrame(Mechanisms.EnvironmentalStress, "現場環境或人員操作",
                                        "無法指出具體觸發條件", "目前沒有任何可量測的觀察"),
                         nameof(LikertBelief.Unlikely), Array.Empty<int>(), Array.Empty<int>()));

        return new JsonObject { ["hypotheses"] = arr }.ToJsonString();
    }

    private static JsonObject Node(ClaimFrame f, string conf, int[] forIdx, int[] against) => new()
    {
        ["mechanism"] = f.Mechanism, ["locus"] = f.Locus, ["trigger"] = f.Trigger, ["observable"] = f.Observable,
        ["confidence"] = conf, ["evidenceFor"] = Ids(forIdx), ["evidenceAgainst"] = Ids(against)
    };

    private static JsonArray Ids(int[] idx)
    {
        var a = new JsonArray();
        foreach (var i in idx) a.Add($"EV-{i:000}");
        return a;
    }

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
    /// designer「賭」實驗會區分第一個假設：H1 在 outcome[0] 投 AlmostCertain，其餘投 Unlikely。
    /// 若真相不是 H1，模擬器會回 outcome[1]，後驗就離開 H1 —— 設計者會被 Evaluation 判錯。
    /// 注意：它只投等級，數字由確定性程式查表定案。
    /// </summary>
    private static string DesignerJson(IReadOnlyList<string> claimIds)
    {
        if (claimIds.Count == 0) return """{"experiments":[]}""";
        var votes = new JsonObject();
        for (int i = 0; i < claimIds.Count; i++)
            votes[claimIds[i]] = i == 0
                ? new JsonArray(nameof(LikertBelief.AlmostCertain), nameof(LikertBelief.AlmostImpossible))
                : new JsonArray(nameof(LikertBelief.Unlikely), nameof(LikertBelief.Likely));

        // 第二個實驗刻意沒有區分力：每個假設在兩個結果下的等級都一樣。
        // 它有成本、看起來很忙，但無論出哪個結果都不會改變推薦行動 → EVOI ≤ 0，應該被跳過。
        // 這是 MR-16 在 benchmark 上的掛鉤點：沒有這個提名，EVOI 選擇器就沒有東西可以擋。
        var flat = new JsonObject();
        foreach (var id in claimIds)
            flat[id] = new JsonArray(nameof(LikertBelief.EvenOdds), nameof(LikertBelief.EvenOdds));

        return new JsonObject
        {
            ["experiments"] = new JsonArray(
                new JsonObject
                {
                    ["description"] = "以受控條件重現並注入修復，比對修復前後的失效率",
                    ["outcomes"] = new JsonArray("失效率顯著下降", "無明顯變化"),
                    ["cost"] = 0.4,
                    ["votes"] = votes
                },
                new JsonObject
                {
                    ["description"] = "再蒐集一週的同類運行紀錄並重新統計",
                    ["outcomes"] = new JsonArray("紀錄增加", "紀錄未增加"),
                    ["cost"] = 0.3,
                    ["votes"] = flat
                })
        }.ToJsonString();
    }

    /// <summary>三種 payload 都刻意寫成對稱的：情境證據數與篇幅相當，領先指標有來源、閾值、頻率。</summary>
    private static string Brief(string systemPrompt)
        => systemPrompt.Contains("不可做方向預測") ? """
            {"facts":[{"text":"以下僅為可查證的事實，未包含任何方向判斷","evidenceFor":["EV-001","EV-002"]}],
             "scenarios":[
               {"name":"情境 A","basis":[{"text":"若現有趨勢延續，對應的觀察應為指標維持在近期區間內","evidenceFor":["EV-001","EV-002"]}],
                "indicators":[{"name":"指標 A 的週變動幅度","source":"EV-002 的同一資料來源","threshold":"單週變動 > 10%","frequency":"每週"}]},
               {"name":"情境 B","basis":[{"text":"若條件反轉，對應的觀察應為指標跌出近期區間下緣","evidenceFor":["EV-001","EV-002"]}],
                "indicators":[{"name":"指標 B 的連續下降天數","source":"EV-001 的同一資料來源","threshold":"連續 5 日下降","frequency":"每日"}]}],
             "risk":{"exposure":"部位在任一情境下都會受到波動影響","exitCondition":"觸及事前設定的最大可承受回撤即退出"}}
            """
         : systemPrompt.Contains("沒有各方同意的判準") ? """
            {"items":[
               {"name":"可量測的成本與時間","whoItMattersTo":"承擔執行成本的一方","evidenceFor":["EV-001"]},
               {"name":"難以量化但當事人在意的部分","whoItMattersTo":"直接受影響的個人","evidenceFor":["EV-002"]}],
             "conflicts":[{"between":"可量化指標 vs 當事人重視的價值","nature":"兩者的最佳解指向不同方向，無法同時最大化"}],
             "forHumans":[{"question":"以哪一組判準為主？","whoDecides":"要承擔後果的人"}]}
            """
         : systemPrompt.Contains("爭議在定義本身") ? """
            {"definitions":[
               {"name":"定義 A：以可計算的總量為準","statement":"把爭議化約成可加總的量再比較","consequence":"會得到可計算但未必被各方接受的結論","evidenceFor":["EV-001"]},
               {"name":"定義 B：以不可化約的底線為準","statement":"某些項目不得被量化比較","consequence":"在此框架下問題本身不成立","evidenceFor":["EV-002"]}],
             "whyUndecidable":"兩種定義各自自洽，選哪一個是價值判斷，不是可檢核的事實問題。"}
            """
         : "{}";

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

    // ── L3 模擬器：依 Mechanism 判定，不再靠自然語言關鍵字 ──
    public static Func<Claim, CaseState, (bool, double, string)?> TestVerifier(BenchCase c) => (claim, _) =>
    {
        if (claim.Kind != ClaimKind.Hypothesis || claim.IsOpinion) return null;
        int idx = HypIndex(c, claim.Frame);
        if (idx == 0) return null;                                    // 不在宣告清單內 → 測試不適用
        return idx == c.TrueHypothesis
            ? (true, 0.9, $"可執行測試：在受控條件下依「{claim.Frame.Observable}」重現，注入修復後歸零")
            : (false, 0.2, $"可執行測試：受控條件下未觀察到「{claim.Frame.Observable}」，與現場型態不符");
    };

    // ── L4 實驗：真相是 H1 → 觀察到 outcome[0]；否則 outcome[1] ──
    public static Func<Experiment, CaseState, CancellationToken, Task<int>> ExperimentRunner(BenchCase c)
        => (_, _, _) => Task.FromResult(c.TrueHypothesis == 1 ? 0 : 1);

    /// <summary>比對用四元組的 Mechanism|Locus，不是自然語言。</summary>
    private static int HypIndex(BenchCase c, ClaimFrame frame)
    {
        for (int i = 0; i < c.Hypotheses.Length; i++)
            if (c.Hypotheses[i].Frame.Identity == frame.Identity) return i + 1;
        return 0;
    }
}
