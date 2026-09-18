// ============================================================================
//  Host（CLI）— Rev2 Phase 1 離線 demo：全部走 ScriptedLlm，不連網。
//  dotnet run --project src/DecisionAI.Host
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using DecisionAI.Core.Domain;
using DecisionAI.Modules.Humans;
using DecisionAI.Modules.Probability;
using DecisionAI.Orchestration;
using DecisionAI.Testing;

Console.OutputEncoding = Encoding.UTF8;

var sys = TestSystem.Build(new TestSystemOptions { Human = new ConsoleHumanGateway() });

// ════════ 類別 1：工程根因分析（Catalog 對映 + 預登記 hash + L4）════════
var c1 = await sys.Orchestrator.RunAsync("CASE-001", AtsScenario.Request(), AtsScenario.Evidence());
Print(c1);

if (c1.State.Decision?.RecommendedAction is string action)
{
    var (cycles, failures) = sys.World.Deploy(action, cycles: 100_000);
    // 真相以 mechanism 表示（競態），再對映到本案的本地編號——本地編號每次可能不同
    string trueId = c1.State.Claims.First(k => k.Frame.Mechanism == Mechanisms.RaceCondition).LocalId;
    var truth = c1.State.Claims.ToImmutableDictionary(k => k.LocalId, k => k.LocalId == trueId);
    var next = sys.Orchestrator.RecordOutcome(c1.Journal, new Outcome(trueId, truth, VerifierLevel.L5_RealOutcome,
        $"部署 {action} 後 {cycles:N0} 次循環，失效 {failures} 次"));
    Console.WriteLine("\n" + string.Join("\n", c1.State.Log.Where(l => l.StartsWith("[outcome]"))));
    Console.WriteLine("\n" + next.Report());
}

// ════════ Triage 拒答 + 型別化替代交付 ════════
var c2 = await sys.Orchestrator.RunAsync("CASE-002", new DecisionRequest(
    "下週某上市公司股價會漲還是跌？", "決定要不要加碼", "finance",
    new Constraints(1.0, 60, RiskLevel.High), Reflexive: true), AtsScenario.Evidence());
Print(c2);

// ════════ Tool/Model Router：轉介專業數值模型 ════════
var c3 = await sys.Orchestrator.RunAsync("CASE-003", new DecisionRequest(
    "某繼電器已運行 80 萬次，未來一個月故障機率是多少？", "決定是否計畫性更換", "reliability",
    new Constraints(1.0, 60, RiskLevel.Medium),
    Specialist: new SpecialistDomain("weibull_reliability",
        "壽命分布有成熟的參數模型，LLM 算不出也不該算",
        "取 shape/scale 與 80 萬次處的條件失效機率，看 95% 信賴區間寬度",
        "樣本 37 顆、右設限比例高時區間會很寬",
        ImmutableArray.Create("同型號失效循環數清單", "目前累計循環數", "月增循環數"))),
    Array.Empty<EvidenceDraft>());
Print(c3);

static void Print(CaseRun run)
{
    var s = run.State;
    Console.WriteLine($"\n══════ {s.Id}：{s.Request!.Problem} ══════");
    foreach (var l in s.Log.Where(l => !l.StartsWith("[outcome]"))) Console.WriteLine("  " + l);

    if (s.Claims.Count > 0)
    {
        Console.WriteLine("\n  主張（四元組 → catalog id）：");
        foreach (var k in s.Claims)
            Console.WriteLine($"    {k.LocalId} {(k.IsOpinion ? "[意見]" : "")} {k.Frame.Render()}\n" +
                              $"        → {k.Key}（{k.Match}）  ← {string.Join(", ", k.Proposals.Select(p => $"{p.AgentId}:{Likert.Label(p.Confidence)}"))}");
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
    if (r.Capability is { } cap)
    {
        Console.WriteLine($"    能力層：{cap.BestPassedLevel} 上限 {cap.Cap:F2}{(cap.Degraded ? "（降級）" : "")}");
        foreach (var why in cap.DegradedReasons) Console.WriteLine($"      · {why}");
    }
    Console.WriteLine($"    覆蓋層：{(r.Coverage is null ? "n/a（校準樣本不足或題型不在白名單）" : $"{r.Coverage.TargetCoverage:P0} {{{string.Join(", ", r.Coverage.PredictionSet)}}}")}");
    if (r.Stability is { } st)
    {
        // 三種擾動分開列：合成一個數字就看不出是哪一種擾動讓結論翻掉
        Console.WriteLine($"    穩定層：機率擾動 {st.Robustness:P0}  最壞 {st.WorstCase:F0}  最大後悔 {st.MaxRegret:F0}");
        Console.WriteLine($"          　假設集重抽樣 {(st.DecisionStabilityUnderResampling is { } q ? q.ToString("P0") : "n/a")}；" +
                          $"似然 ±1 級排序{(st.PosteriorOrderStableUnderLikertShift switch { true => "不變", false => "會變（結論撐在估計值上）", _ => " n/a" })}");
    }
    if (r.HypothesisCoverage is { } hc)
        Console.WriteLine($"    假設覆蓋：singletons {hc.Singletons}、doubletons {hc.Doubletons}、估計未發現 {hc.EstimatedUndiscovered:F1}");
    if (r.Abstention.Abstained)
    {
        Console.WriteLine($"    拒答：{r.Abstention.Code} — {r.Abstention.Explanation}");
        Console.WriteLine($"    替代交付：{r.Abstention.Payload?.Kind ?? "（無）"}");
        if (r.Abstention.Payload is DecisionAI.Core.Assurance.ScenarioBriefing sb)
            foreach (var sc in sb.Scenarios)
                Console.WriteLine($"      情境「{sc.Name}」：{sc.Basis.Sum(b => b.EvidenceFor.Length)} 條證據、" +
                                  $"{sc.Indicators.Length} 個領先指標");
    }
    Console.WriteLine($"  成本 {s.CostSpent:F3} / 預算 {s.Request.Constraints.Budget:F1}；" +
                      $"步驟 {string.Join(" ", s.StepStatus.OrderBy(k => s.PlannedSteps.IndexOf(k.Key)).Select(x => $"{x.Key}={x.Value}"))}");
    if (s.Halted) Console.WriteLine($"  ⚠ 中止：{s.HaltReason}；未執行：{string.Join(", ", s.SkippedSteps)}");
    Console.WriteLine($"  事件 {run.Journal.Events.Count} 筆（policy v{s.PolicyVersion} / claim catalog v{s.ClaimCatalogVersion}）");
}
