// ============================================================================
//  Demo：ATS 每 ~300 次循環出現一次 AMQP 斷線 —— 從 Request 到 Outcome 的完整閉環。
//  第一版只有 5 種角色：profiler / solver ×3 / critic / experiment_designer（+ synthesizer 給反身流程用）。
//  閉環比 agent 數量重要。
//
//  dotnet run    無人值守：人類核准自動同意；在終端機互動執行時會真的問你。
// ============================================================================

using System.Text;
using DecisionAI.Agents;
using DecisionAI.App;
using DecisionAI.Decision;
using DecisionAI.Domain;
using DecisionAI.Evaluation;
using DecisionAI.Probability;
using DecisionAI.Routing;
using DecisionAI.Store;
using DecisionAI.Verification;
using DecisionAI.Workflow;

namespace DecisionAI;

public static class Program
{
    public static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ── 組裝（Modular Monolith；正式版換成 DI 容器，建構子不變）──
        var store       = new EvidenceStore();
        var registry    = BuildRegistry();
        var runner      = new AgentRunner();
        var calibration = new CalibrationEngine();
        var ensemble    = new EnsembleEngine(registry, calibration);
        var bayes       = new BayesianEngine { Temper = 0.7 };
        var decision    = new DecisionEngine();
        var evaluation  = new EvaluationSystem(registry, calibration);
        var world       = new AtsSimulator();                       // 真實系統的替身：L4 實驗、L5 部署結果都問它

        var verifiers = new VerifierRegistry();
        verifiers.Register(new RuleVerifier(store));                                     // L2
        verifiers.Register(new LlmCriticVerifier(registry, runner));                     // L1
        verifiers.Register(new ExperimentVerifier(store, (exp, c, ct) => Task.FromResult(world.RunExperiment(exp))));  // L4

        var engine = new WorkflowEngine();
        new StandardHandlers(registry, runner, verifiers, ensemble, bayes, decision, ConsoleApprove).RegisterAll(engine);
        var router = new Router(registry, runner, WorkflowCatalog.Default());

        // ════════ Case 1：工程根因分析 ════════
        var c1 = new DecisionCase(new DecisionRequest(
            Problem: "ATS 每運行約 300 次循環會出現一次 AMQP 斷線",
            Goal: "找到最可能的根因、設計驗證實驗、選出修復行動",
            Domain: "csharp_concurrency",
            Constraints: new Constraints(Budget: 2.0, MaxLatencySeconds: 120, RiskLevel: RiskLevel.High),
            Actions: new[]
            {   // 效用矩陣：人給的。情境 = 假設 ID（Router 之後假設會依 H1..H4 順序出現）
                new ActionOption("A", "修改 KeepAlive timer 的鎖（根因修復）", new Dictionary<string, double> { ["H1"] = 100, ["H2"] = -20, ["H3"] = -30, ["H4"] = -30 }),
                new ActionOption("B", "移除 KeepAlive（workaround）",       new Dictionary<string, double> { ["H1"] =  60, ["H2"] =  10, ["H3"] = -30, ["H4"] = -30 }),
                new ActionOption("C", "外層 retry 包裝（治標）",             new Dictionary<string, double> { ["H1"] =  20, ["H2"] =  20, ["H3"] =  30, ["H4"] =  30 }),
            },
            RiskPolicy: new RiskPolicy(MaxAcceptableLoss: 50)));

        LoadAtsEvidence(store, c1);
        var wf = await router.PlanAsync(c1, verifiers.BestAvailable());
        await engine.RunAsync(wf, c1);
        Print(c1);

        // ── 部署後真實結果（L5）→ Evaluation 回饋 ──
        if (c1.Decision?.RecommendedAction is string action)
        {
            var (cycles, failures) = world.Deploy(action, cycles: 100_000);
            evaluation.RecordOutcome(c1, new Outcome(
                TrueClaimId: "H1",
                ClaimTruth: c1.Claims.ToDictionary(k => k.Id, k => k.Id == "H1"),
                Level: VerifierLevel.L5_RealOutcome,
                Note: $"部署 {action} 後 {cycles:N0} 次循環，失效 {failures} 次", At: DateTime.UtcNow));
            Console.WriteLine("\n" + string.Join("\n", c1.Log.Where(l => l.StartsWith("[eval]"))));
        }

        Console.WriteLine("\n" + evaluation.Report());
        Console.WriteLine("\n下次同領域的 solver 排序：" + string.Join(" > ", registry.Select("solver", "csharp_concurrency", 3).Select(a => a.AgentId)));

        // ════════ Case 2：反身系統 → 不做方向預測 ════════
        var c2 = new DecisionCase(new DecisionRequest(
            Problem: "下週某上市公司股價會漲還是跌？", Goal: "決定要不要加碼", Domain: "finance",
            Constraints: new Constraints(1.0, 60, RiskLevel.High), Reflexive: true));
        var wf2 = await router.PlanAsync(c2, verifiers.BestAvailable());
        await engine.RunAsync(wf2, c2);
        Print(c2);
    }

    // ── Agent Registry：5 種角色、3 個「廠商」。冷啟動下所有人 Beta(1,1)，選擇靠異質性 ──
    private static AgentRegistry BuildRegistry()
    {
        var r = new AgentRegistry();
        r.Register(new AgentSpec("solver-A", "vendor-x", new FakeLlm("x-large", 11, Persona("A")), new[] { "solver", "profiler", "synthesizer" }, new[] { "csharp_concurrency", "distributed_system" }, new[] { "code_execution" }, 1.0, 3.0));
        r.Register(new AgentSpec("solver-B", "vendor-y", new FakeLlm("y-large", 12, Persona("B")), new[] { "solver" },             new[] { "distributed_system", "*" },               new[] { "log_search" },     0.8, 2.4));
        r.Register(new AgentSpec("solver-C", "vendor-z", new FakeLlm("z-medium", 13, Persona("C")), new[] { "solver", "analyst" }, new[] { "*" },                                     Array.Empty<string>(),      0.3, 0.9));
        r.Register(new AgentSpec("critic-D", "vendor-y", new FakeLlm("y-small", 14, Persona("D")), new[] { "critic", "analyst", "synthesizer" }, new[] { "*" },                    Array.Empty<string>(),      0.2, 0.6));
        r.Register(new AgentSpec("designer-E", "vendor-x", new FakeLlm("x-medium", 15, Persona("E")), new[] { "experiment_designer", "analyst" }, new[] { "csharp_concurrency", "*" }, Array.Empty<string>(),   0.5, 1.5));
        return r;
    }

    // ── 假模型的人格：只回傳格式正確的合成 JSON，讓每條路徑跑通 ──
    private static Func<LlmRequest, Random, string> Persona(string who) => (req, rng) => req.Role switch
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
        "profiler"    => """{"domain":"csharp_concurrency"}""",
        "analyst"     => $"[{who}] {(req.System.Contains("視角：") ? req.System.Split("視角：")[1] : "分析")}：要點 1) … 2) … 3) …（{rng.Next(100)}）",
        "synthesizer" => "【決策備忘】\n" + req.User.Replace("\n---\n", "\n") + "\n（無方向結論；由人決定）",
        _             => $"[{who}] {req.User[..Math.Min(40, req.User.Length)]}…"
    };

    private static void LoadAtsEvidence(EvidenceStore store, DecisionCase c)
    {
        c.Evidence.Add(store.Add(EvidenceSource.RuntimeLog, "ATS-PC-03",     "斷線前 20 ms 內 KeepAlive timer callback（thread 12）與 CloseLink（thread 7）交錯執行"));
        c.Evidence.Add(store.Add(EvidenceSource.SourceCode, "AmqpClient.cs", "KeepAliveTick() 直接讀寫 _link，沒有取得 _linkLock"));
        c.Evidence.Add(store.Add(EvidenceSource.RuntimeLog, "switch-01",     "同時段交換機無 port flap、無 ICMP 丟包"));
        c.Evidence.Add(store.Add(EvidenceSource.RuntimeLog, "PLC-log",       "斷線發生時 PLC 側狀態仍為 Connected；斷線由 ATS 端主動關閉"));
        c.Evidence.Add(store.Add(EvidenceSource.UserInput,  "user",          "約每 300 次循環一次；每次循環 2 秒 → 週期約 10 分鐘"));
    }

    private static bool ConsoleApprove(DecisionCase c, string what)
    {
        if (Console.IsInputRedirected) return true;   // 無人值守
        Console.Write($"\n  [核准] {what}？(y/N) ");
        return (Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static void Print(DecisionCase c)
    {
        Console.WriteLine($"\n══════ {c.Id}：{c.Request.Problem} ══════");
        foreach (var l in c.Log.Where(l => !l.StartsWith("[eval]"))) Console.WriteLine("  " + l);
        Console.WriteLine($"\n  主張：");
        foreach (var k in c.Claims.Where(k => k.Kind == ClaimKind.Hypothesis))
            Console.WriteLine($"    {k.Id} {(k.IsOpinion ? "[意見]" : "")} {k.Statement}  ← {string.Join(", ", k.Proposals.Select(p => $"{p.AgentId}:{p.StatedConfidence:F2}"))}");
        if (c.Beliefs.Count > 0) Console.WriteLine($"  信念：{EnsembleEngine.Render(c.Beliefs)}");
        if (c.Decision is { } d)
        {
            Console.WriteLine($"  決策：{d.RecommendedAction}  EU={d.ExpectedUtility:F1}  最壞={d.WorstCase:F0}  最大後悔={d.MaxRegret:F0}  穩健性={d.Robustness:P0}  信心={d.Confidence:F2}");
            foreach (var kv in d.PerAction) Console.WriteLine($"    {kv.Key}: EU={kv.Value.ExpectedUtility,6:F1} 最壞={kv.Value.WorstCase,4:F0} 後悔={kv.Value.MaxRegret,4:F0} {(kv.Value.Admissible ? "" : "（超過可接受損失）")}");
        }
        if (c.Memo is not null) Console.WriteLine("  備忘：\n    " + c.Memo.Replace("\n", "\n    "));
        Console.WriteLine($"  成本 {c.CostSpent:F3} / 預算 {c.Request.Constraints.Budget:F1}；步驟 {string.Join(" ", c.StepStatus.Select(s => $"{s.Key}={s.Value}"))}");
        if (c.Halted) Console.WriteLine($"  ⚠ 中止：{c.HaltReason}");
    }
}

/// <summary>真實系統的替身。隱藏真相 = H1（timer race）。正式版：這就是你的 HIL / staging 環境。</summary>
public sealed class AtsSimulator
{
    private readonly Random _rng = new(2026);

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
