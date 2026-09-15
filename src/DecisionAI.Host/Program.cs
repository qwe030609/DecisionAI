// ============================================================================
//  Host（CLI）— Phase 1 離線 demo：全部走 ScriptedLlm，不連網。
//  dotnet run --project src/DecisionAI.Host
//  無人值守：人類核准自動同意；在終端機互動執行時會真的問你。
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Modules.Humans;
using DecisionAI.Modules.Probability;
using DecisionAI.Orchestration;
using DecisionAI.Testing;

Console.OutputEncoding = Encoding.UTF8;

var sys = TestSystem.Build(new TestSystemOptions { Human = new ConsoleHumanGateway() });

// ════════ 類別 1：工程根因分析（有 L3 模擬器 + L4 實驗）════════
var c1 = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
Print(c1);

// ── 部署後真實結果（L5）→ Evaluation → PolicySnapshot v1 ──
if (c1.State.Decision?.RecommendedAction is string action)
{
    var (cycles, failures) = sys.World.Deploy(action, cycles: 100_000);
    var truth = c1.State.Claims.ToImmutableDictionary(k => k.Id, k => k.Id == "H1");
    var next = sys.Orchestrator.RecordOutcome(c1.Journal, new Outcome("H1", truth, VerifierLevel.L5_RealOutcome,
        $"部署 {action} 後 {cycles:N0} 次循環，失效 {failures} 次"));
    Console.WriteLine("\n" + string.Join("\n", c1.State.Log.Where(l => l.StartsWith("[outcome]"))));
    Console.WriteLine("\n" + next.Report());
    Console.WriteLine("\n下次同領域的 solver 排序（policy v" + next.Version + "）：" +
        string.Join(" > ", sys.Registry.Select(new("solver", AtsScenario.Domain, 3), next).Select(a => a.Spec.AgentId)));
}

// ════════ Triage 拒絕：反身系統 / 無 ground truth / 定義爭議 ════════
var c2 = await sys.Orchestrator.RunAsync("CASE-002", new DecisionRequest(
    "下週某上市公司股價會漲還是跌？", "決定要不要加碼", "finance", new Constraints(1.0, 60, RiskLevel.High), Reflexive: true), Array.Empty<EvidenceDraft>());
Print(c2);

var c3 = await sys.Orchestrator.RunAsync("CASE-003", new DecisionRequest(
    "公司 logo 該用藍色還是綠色？", "定案", "creative", new Constraints(1.0, 60, RiskLevel.Low), GroundTruth: GroundTruthStatus.Undefined), Array.Empty<EvidenceDraft>());
Print(c3);

// ════════ 類別 4：跨領域流水線 + 邊界閘（人類在 console 可拒絕 → 下游不執行）════════
var stages = ImmutableArray.Create(
    new StageSpec("量測", "設計 SCPI 量測序列", VerifierLevel.L3_ExecutableTest, "輸出：CSV 欄位 [t, V, I]"),
    new StageSpec("分析", "由量測資料估算等效電阻", VerifierLevel.L3_ExecutableTest, "輸出：R ± σ"),
    new StageSpec("報告", "產出結論段落", VerifierLevel.L1_LlmCritic, "輸出：≤ 200 字"));
var c4 = await sys.Orchestrator.RunAsync("CASE-004", new DecisionRequest(
    "量測待測物等效電阻並出報告", "三段流水線", "instrument_control", new Constraints(2.0, 120, RiskLevel.Medium), Stages: stages), Array.Empty<EvidenceDraft>());
Print(c4);

static void Print(CaseRun run)
{
    var s = run.State;
    Console.WriteLine($"\n══════ {s.Id}：{s.Request!.Problem} ══════");
    foreach (var l in s.Log.Where(l => !l.StartsWith("[outcome]"))) Console.WriteLine("  " + l);
    if (s.Claims.Count > 0)
    {
        Console.WriteLine("\n  主張：");
        foreach (var k in s.Claims)
            Console.WriteLine($"    {k.Id} {(k.IsOpinion ? "[意見]" : "")} {Trim(k.Statement)}  ← {string.Join(", ", k.Proposals.Select(p => $"{p.AgentId}:{p.StatedConfidence:F2}"))}");
    }
    if (s.Beliefs.Count > 0) Console.WriteLine($"  信念：{Beliefs.Render(s.Beliefs)}");
    if (s.Decision is { } d)
    {
        Console.WriteLine($"  決策：{d.RecommendedAction}  EU={d.ExpectedUtility:F1}  最壞={d.WorstCase:F0}  最大後悔={d.MaxRegret:F0}");
        foreach (var kv in d.PerAction.OrderBy(k => k.Key))
            Console.WriteLine($"    {kv.Key}: EU={kv.Value.ExpectedUtility,6:F1} 最壞={kv.Value.WorstCase,4:F0} 後悔={kv.Value.MaxRegret,4:F0} {(kv.Value.Admissible ? "" : "（超過可接受損失）")}");
    }
    var r = run.Report;
    Console.WriteLine($"  Assurance：{r.ConservativeGrade()}");
    if (r.Capability is { } cap) Console.WriteLine($"    能力層：{cap.BestPassedLevel} 上限 {cap.Cap:F2}{(cap.Degraded ? "（降級）" : "")}");
    Console.WriteLine($"    覆蓋層：{(r.Coverage is null ? "n/a（Phase 2）" : r.Coverage.TargetCoverage.ToString("P0"))}");
    if (r.Stability is { } st) Console.WriteLine($"    穩定層：穩健性 {st.Robustness:P0}  最壞 {st.WorstCase:F0}  最大後悔 {st.MaxRegret:F0}");
    if (r.Abstention.Abstained) Console.WriteLine($"    拒答：{r.Abstention.Code} — {r.Abstention.Explanation}\n    替代：{r.Abstention.Alternative}");
    Console.WriteLine($"  成本 {s.CostSpent:F3} / 預算 {s.Request.Constraints.Budget:F1}；步驟 {string.Join(" ", s.StepStatus.OrderBy(k => s.PlannedSteps.IndexOf(k.Key)).Select(x => $"{x.Key}={x.Value}"))}");
    if (s.Halted) Console.WriteLine($"  ⚠ 中止：{s.HaltReason}；未執行：{string.Join(", ", s.SkippedSteps)}");
    Console.WriteLine($"  事件 {run.Journal.Events.Count} 筆（policy v{s.PolicyVersion}）");
}

static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s.Replace("\n", " ");
