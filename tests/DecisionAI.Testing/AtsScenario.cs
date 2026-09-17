// ============================================================================
//  類別 1 的標準案例：ATS 每 ~300 次循環出現一次 AMQP 斷線。
//  Rev2：solver 輸出四元組與定性等級；Catalog 預先播種三條已知故障模式（等同一份 FMEA 表）。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Catalog;

namespace DecisionAI.Testing;

public static class AtsScenario
{
    public const string Domain = "csharp_concurrency";

    // ── 四個候選故障模式（H4 刻意不在 Catalog 裡，用來看 New 的路徑）──
    public static readonly ClaimFrame RaceFrame = new(
        Mechanisms.RaceCondition, "AmqpClient.KeepAliveTick/_link",
        "併發存取：timer callback 與 CloseLink 同時碰同一個連線物件",
        "thread log 上 KeepAlive callback 與 CloseLink 的時序重疊");

    public static readonly ClaimFrame LifecycleFrame = new(
        Mechanisms.LifecycleMisuse, "AmqpClient._link channel",
        "長時間執行後 channel 被提前回收",
        "斷線前 channel 已進入 Closed 狀態");

    public static readonly ClaimFrame NetworkFrame = new(
        Mechanisms.EnvironmentalStress, "switch-01 網路路徑",
        "長時間閒置觸發 idle timeout",
        "交換機 port flap 或 ICMP 丟包紀錄");

    public static readonly ClaimFrame PlcFrame = new(
        Mechanisms.ProtocolViolation, "PLC 端連線",
        "週期性重新協商",
        "PLC 側主動送出 close frame");

    /// <summary>Catalog 種子：已知的三類故障模式。H4 不在裡面 → 會走 New 的路徑。</summary>
    public static InMemoryClaimCatalog SeededCatalog() => InMemoryClaimCatalog.Seeded(
        (Domain, RaceFrame with { Trigger = "併發存取", Observable = "執行緒時序重疊" }),
        (Domain, LifecycleFrame with { Trigger = "資源提前釋放", Observable = "物件狀態為已關閉" }),
        (Domain, NetworkFrame with { Trigger = "閒置逾時", Observable = "鏈路層異常紀錄" }));

    public static DecisionRequest Request() => new(
        Problem: "ATS 每運行約 300 次循環會出現一次 AMQP 斷線",
        Goal: "找到最可能的根因、設計驗證實驗、選出修復行動",
        Domain: Domain,
        Constraints: new Constraints(Budget: 3.0, MaxLatencySeconds: 120, RiskLevel: RiskLevel.High),
        Actions: ImmutableArray.Create(
            Action("A", "修改 KeepAlive timer 的鎖（根因修復）", ("H1", 100), ("H2", -20), ("H3", -30), ("H4", -30)),
            Action("B", "移除 KeepAlive（workaround）",       ("H1", 60), ("H2", 10), ("H3", -30), ("H4", -30)),
            Action("C", "外層 retry 包裝（治標）",             ("H1", 20), ("H2", 20), ("H3", 30), ("H4", 30))),
        RiskPolicy: new RiskPolicy(MaxAcceptableLoss: 50));

    private static ActionOption Action(string id, string desc, params (string, double)[] u)
        => new(id, desc, u.ToImmutableDictionary(x => x.Item1, x => x.Item2));

    public static IReadOnlyList<EvidenceDraft> Evidence() => new[]
    {
        new EvidenceDraft(EvidenceSource.RuntimeLog, "ATS-PC-03",     "斷線前 20 ms 內 KeepAlive timer callback（thread 12）與 CloseLink（thread 7）交錯執行"),
        new EvidenceDraft(EvidenceSource.SourceCode, "AmqpClient.cs", "KeepAliveTick() 直接讀寫 _link，沒有取得 _linkLock"),
        new EvidenceDraft(EvidenceSource.RuntimeLog, "switch-01",     "同時段交換機無 port flap、無 ICMP 丟包"),
        new EvidenceDraft(EvidenceSource.RuntimeLog, "PLC-log",       "斷線發生時 PLC 側狀態仍為 Connected；斷線由 ATS 端主動關閉"),
        new EvidenceDraft(EvidenceSource.UserInput,  "user",          "約每 300 次循環一次；每次循環 2 秒 → 週期約 10 分鐘"),
    };

    /// <summary>五個 agent、三個廠商、五個模型家族。冷啟動下所有人 Beta(1,1)，選擇靠異質性。</summary>
    public static IEnumerable<(AgentSpec Spec, ScriptedLlm Llm)> Agents()
    {
        yield return (Spec("solver-A",   "vendor-x", "x-large",  new[] { "solver" },                        new[] { Domain, "distributed_system" }, 1.0, 3.0), Persona("x-large",  "A"));
        yield return (Spec("solver-B",   "vendor-y", "y-large",  new[] { "solver" },                        new[] { "distributed_system", "*" },    0.8, 2.4), Persona("y-large",  "B"));
        yield return (Spec("solver-C",   "vendor-z", "z-medium", new[] { "solver", "briefer" },             new[] { "*" },                          0.3, 0.9), Persona("z-medium", "C"));
        yield return (Spec("critic-D",   "vendor-y", "y-small",  new[] { "critic", "briefer" },             new[] { "*" },                          0.2, 0.6), Persona("y-small",  "D"));
        yield return (Spec("designer-E", "vendor-x", "x-medium", new[] { "experiment_designer" },           new[] { Domain, "*" },                  0.5, 1.5), Persona("x-medium", "E"));
    }

    private static AgentSpec Spec(string id, string vendor, string family, string[] roles, string[] domains, double cin, double cout)
        => new(id, vendor, family, "2026Q1", roles.ToImmutableArray(), domains.ToImmutableArray(), new CostProfile(cin, cout));

    /// <summary>假模型的人格：只回傳格式正確的合成 JSON，讓每條路徑跑通。</summary>
    public static ScriptedLlm Persona(string model, string who) => new(model, req => req.Role switch
    {
        "solver" when who == "A" => Hyps(
            (RaceFrame, "AlmostCertain", new[] { 1, 2 }, Array.Empty<int>()),
            (LifecycleFrame, "Unlikely", new[] { 2 }, new[] { 4 })),

        // solver-B 用不同措辭描述同樣的 Mechanism|Locus —— 合併與 canonical id 都必須一致（MR-14）
        "solver" when who == "B" => Hyps(
            (LifecycleFrame with { Trigger = "channel 生命週期管理錯誤", Observable = "連線物件在斷線前已被釋放" }, "Likely", new[] { 2 }, Array.Empty<int>()),
            (RaceFrame with { Trigger = "timer 回呼與關閉流程競爭同一物件", Observable = "兩個執行緒的時序在斷線瞬間重疊" }, "Unlikely", new[] { 1 }, Array.Empty<int>()),
            (NetworkFrame, "AlmostImpossible", new[] { 5 }, new[] { 3 })),

        "solver" => Hyps(
            (RaceFrame, "EvenOdds", new[] { 1, 2 }, Array.Empty<int>()),
            (NetworkFrame, "Unlikely", new[] { 5 }, new[] { 3 }),
            (PlcFrame, "Unlikely", Array.Empty<int>(), Array.Empty<int>())),

        "critic" => """
            {"critiques":[
              {"claimId":"H1","issue":"證據充分；需確認 timer callback 是否可重入","severity":0.2},
              {"claimId":"H2","issue":"EV-004 顯示斷線由 ATS 主動關閉，與 H1 同樣相容，區分力不足","severity":0.5},
              {"claimId":"H3","issue":"EV-003 直接反駁：同時段無 port flap","severity":0.8},
              {"claimId":"H4","issue":"沒有任何證據；EV-004 反駁","severity":0.9}]}
            """,

        "experiment_designer" => """
            {"experiments":[
              {"description":"停用 KeepAlive timer，連跑 5000 次循環","outcomes":["失效率下降 >80%","無明顯變化"],"cost":0.4,
               "votes":{"H1":["AlmostCertain","AlmostImpossible"],"H2":["Unlikely","Likely"],"H3":["AlmostImpossible","AlmostCertain"]}}]}
            """,

        "briefer" => Brief(req.System),
        Actors_StageSolver => $"[{who}] 段落交付物：{Between(req.User, "本段任務：", "\n")}",
        _ => $"[{who}] {req.User[..Math.Min(40, req.User.Length)]}…"
    });

    private const string Actors_StageSolver = "stage_solver";

    private static string Hyps(params (ClaimFrame Frame, string Conf, int[] For, int[] Against)[] items)
    {
        var parts = items.Select(h =>
            $$"""
            {"mechanism":"{{h.Frame.Mechanism}}","locus":"{{h.Frame.Locus}}","trigger":"{{h.Frame.Trigger}}",
             "observable":"{{h.Frame.Observable}}","confidence":"{{h.Conf}}",
             "evidenceFor":[{{string.Join(",", h.For.Select(i => $"\"EV-{i:000}\""))}}],
             "evidenceAgainst":[{{string.Join(",", h.Against.Select(i => $"\"EV-{i:000}\""))}}]}
            """);
        return $$"""{"hypotheses":[{{string.Join(",", parts)}}]}""";
    }

    /// <summary>briefer 依 ReasonCode 產出不同型別的 payload —— 型別綁定寫死在編譯期，這裡只是餵對形狀。</summary>
    private static string Brief(string systemPrompt)
        => systemPrompt.Contains("不可做方向預測") ? Briefing()
         : systemPrompt.Contains("沒有各方同意的判準") ? Considerations()
         : systemPrompt.Contains("爭議在定義本身") ? Definitions()
         : "{}";

    private static string Considerations() => """
        {"items":[
           {"name":"可辨識度：在低飽和螢幕與戶外光線下的辨識","whoItMattersTo":"現場操作員","evidenceFor":["EV-001"]},
           {"name":"既有識別資產的一致性","whoItMattersTo":"行銷與通路","evidenceFor":["EV-005"]}],
         "conflicts":[{"between":"辨識度 vs 既有識別一致性","nature":"兩者指向不同色系，無法同時最大化"}],
         "forHumans":[{"question":"以哪一個使用情境為主？","whoDecides":"產品負責人"}]}
        """;

    private static string Definitions() => """
        {"definitions":[
           {"name":"定義 A：以最小傷害總量為準","statement":"比較各方案的預期傷害總量","consequence":"會得到可計算但不被部分法規接受的結論","evidenceFor":["EV-004"]},
           {"name":"定義 B：以不得將人命量化為準","statement":"禁止以人數比較生命價值","consequence":"問題本身在此框架下不成立","evidenceFor":["EV-003"]}],
         "whyUndecidable":"兩種定義各自自洽，選哪一個是價值判斷，不是可檢核的事實問題。"}
        """;

    /// <summary>對稱的情境簡報：兩個情境的證據數與篇幅刻意相當，才過得了 L2 的對稱性檢查。</summary>
    private static string Briefing() => """
        {"facts":[{"text":"斷線週期約 10 分鐘，與 KeepAlive 間隔同階","evidenceFor":["EV-001","EV-005"]}],
         "scenarios":[
           {"name":"情境 A：與程式內部時序相關","basis":[{"text":"斷線前後皆有執行緒交錯紀錄，且由本端主動關閉","evidenceFor":["EV-001","EV-004"]}],
            "indicators":[{"name":"斷線瞬間的執行緒重疊次數","source":"ATS runtime log","threshold":"每千次循環 > 1 次","frequency":"每班"}]},
           {"name":"情境 B：與外部鏈路相關","basis":[{"text":"若為外部因素，應同時出現鏈路層異常與對端狀態變化","evidenceFor":["EV-003","EV-004"]}],
            "indicators":[{"name":"交換機 port flap 次數","source":"switch-01 syslog","threshold":"每班 > 0 次","frequency":"每班"}]}],
         "risk":{"exposure":"產線在斷線期間的在製品需重測","exitCondition":"單班斷線 > 3 次即停線檢查"}}
        """;

    private static string Between(string s, string start, string end)
    {
        int i = s.IndexOf(start, StringComparison.Ordinal); if (i < 0) return "";
        i += start.Length;
        int j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }
}
