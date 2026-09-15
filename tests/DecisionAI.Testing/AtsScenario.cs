// ============================================================================
//  類別 1 的標準案例：ATS 每 ~300 次循環出現一次 AMQP 斷線。
//  請求、證據、五個 agent 的腳本人格集中在這裡，Host demo 與 golden 測試共用。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Testing;

public static class AtsScenario
{
    public const string Domain = "csharp_concurrency";

    public static DecisionRequest Request() => new(
        Problem: "ATS 每運行約 300 次循環會出現一次 AMQP 斷線",
        Goal: "找到最可能的根因、設計驗證實驗、選出修復行動",
        Domain: Domain,
        Constraints: new Constraints(Budget: 2.0, MaxLatencySeconds: 120, RiskLevel: RiskLevel.High),
        Actions: ImmutableArray.Create(
            // 效用矩陣：人給的。情境 = 假設 ID（依 solver 選擇順序，H1..H4）
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

    /// <summary>五個 agent、三個廠商、兩個模型家族。冷啟動下所有人 Beta(1,1)，選擇靠異質性。</summary>
    public static IEnumerable<(AgentSpec Spec, ScriptedLlm Llm)> Agents()
    {
        yield return (Spec("solver-A",   "vendor-x", "x-large",  new[] { "solver" },                          new[] { Domain, "distributed_system" }, 1.0, 3.0), Persona("x-large",  "A"));
        yield return (Spec("solver-B",   "vendor-y", "y-large",  new[] { "solver" },                          new[] { "distributed_system", "*" },    0.8, 2.4), Persona("y-large",  "B"));
        yield return (Spec("solver-C",   "vendor-z", "z-medium", new[] { "solver" },                          new[] { "*" },                          0.3, 0.9), Persona("z-medium", "C"));
        yield return (Spec("critic-D",   "vendor-y", "y-small",  new[] { "critic" },                          new[] { "*" },                          0.2, 0.6), Persona("y-small",  "D"));
        yield return (Spec("designer-E", "vendor-x", "x-medium", new[] { "experiment_designer" },             new[] { Domain, "*" },                  0.5, 1.5), Persona("x-medium", "E"));
    }

    private static AgentSpec Spec(string id, string vendor, string family, string[] roles, string[] domains, double cin, double cout)
        => new(id, vendor, family, "2026Q1", roles.ToImmutableArray(), domains.ToImmutableArray(), new CostProfile(cin, cout));

    /// <summary>假模型的人格：只回傳格式正確的合成 JSON，讓每條路徑跑通。</summary>
    public static ScriptedLlm Persona(string model, string who) => new(model, req => req.Role switch
    {
        "solver" when who == "A" => """
            {"hypotheses":[
              {"statement":"KeepAlive timer callback 與 CloseLink 競爭同一個連線物件（race condition）","confidence":0.80,"evidenceFor":["EV-001","EV-002"],"evidenceAgainst":[]},
              {"statement":"AMQP link 生命週期管理錯誤：channel 被提前回收","confidence":0.15,"evidenceFor":["EV-002"],"evidenceAgainst":["EV-004"]}]}
            """,
        "solver" when who == "B" => """
            {"hypotheses":[
              {"statement":"AMQP link 生命週期管理錯誤：channel 被提前回收","confidence":0.60,"evidenceFor":["EV-002"],"evidenceAgainst":[]},
              {"statement":"KeepAlive timer callback 與 CloseLink 競爭同一個連線物件（race condition）","confidence":0.30,"evidenceFor":["EV-001"],"evidenceAgainst":[]},
              {"statement":"網路瞬斷（交換機 / 防火牆 idle timeout）","confidence":0.10,"evidenceFor":["EV-005"],"evidenceAgainst":["EV-003"]}]}
            """,
        "solver" => """
            {"hypotheses":[
              {"statement":"KeepAlive timer callback 與 CloseLink 競爭同一個連線物件（race condition）","confidence":0.55,"evidenceFor":["EV-001","EV-002"],"evidenceAgainst":[]},
              {"statement":"網路瞬斷（交換機 / 防火牆 idle timeout）","confidence":0.25,"evidenceFor":["EV-005"],"evidenceAgainst":["EV-003"]},
              {"statement":"PLC 端主動斷線","confidence":0.20,"evidenceFor":[],"evidenceAgainst":[]}]}
            """,
        "critic" => """
            {"critiques":[
              {"claimId":"H1","issue":"證據充分；需確認 timer callback 是否可重入","severity":0.2},
              {"claimId":"H2","issue":"EV-004 顯示斷線由 ATS 主動關閉，與 H1 同樣相容，區分力不足","severity":0.5},
              {"claimId":"H3","issue":"EV-003 直接反駁：同時段無 port flap","severity":0.8},
              {"claimId":"H4","issue":"沒有任何證據；EV-004 反駁","severity":0.9}]}
            """,
        "experiment_designer" => """
            {"experiments":[
              {"description":"停用 KeepAlive timer，連跑 5000 次循環","outcomes":["失效率下降 >80%","無明顯變化"],
               "likelihoods":{"H1":[0.90,0.10],"H2":[0.35,0.65],"H3":[0.10,0.90]}},
              {"description":"加入 thread-id 與 CloseLink 時序 log，觀察斷線瞬間 timer callback 是否重疊","outcomes":["有重疊","無重疊"],
               "likelihoods":{"H1":[0.85,0.15],"H2":[0.40,0.60],"H3":[0.10,0.90]}}]}
            """,
        "stage_solver" => $"[{who}] 段落交付物：{Between(req.User, "本段任務：", "\n")}（契約：{Between(req.User, "輸出契約：", "\n")}）",
        _ => $"[{who}] {req.User[..Math.Min(40, req.User.Length)]}…"
    });

    private static string Between(string s, string start, string end)
    {
        int i = s.IndexOf(start, StringComparison.Ordinal); if (i < 0) return "";
        i += start.Length;
        int j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }
}
