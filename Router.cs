// ============================================================================
//  Router — 「這個問題應該怎麼解？」
//  三層，順序固定：規則（事實）→ 歷史政策（Registry 的證據）→ LLM 分類器（只補缺、只出封閉標籤）。
//  ProblemProfile 是事實與類別，不是 8 個小數。允許回傳 SingleAgent。
// ============================================================================

using DecisionAI.Agents;
using DecisionAI.Domain;
using DecisionAI.Workflow;

namespace DecisionAI.Routing;

public sealed class Router
{
    private readonly AgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _workflows;

    public Router(AgentRegistry registry, AgentRunner runner, IReadOnlyDictionary<string, WorkflowDefinition> workflows)
    { _registry = registry; _runner = runner; _workflows = workflows; }

    public async Task<WorkflowDefinition> PlanAsync(DecisionCase c, VerifierLevel bestAvailable, CancellationToken ct = default)
    {
        var r = c.Request;
        var why = new List<string>();

        // ── 層 1：事實。領域沒給才動用 LLM，且輸出限定封閉標籤集，並記錄為可評估的預測 ──
        string domain = r.Domain; string profiledBy = "caller";
        if (string.IsNullOrWhiteSpace(domain))
        {
            var profiler = _registry.Select("profiler", "*", 1).First();
            var run = await _runner.RunAsync(profiler, "profiler", RolePrompts.Profiler, r.Problem, c, 0.0, ct);
            domain = AgentOutputs.Domain(run.RawOutput) ?? "general"; profiledBy = profiler.AgentId;
            why.Add($"領域由 {profiler.AgentId} 標記為 {domain}（記錄為預測，事後可評估）");
        }

        bool hasUtilities = r.Actions is { Count: > 0 };
        c.Profile = new ProblemProfile(bestAvailable, r.Reflexive, r.Probabilistic, hasUtilities,
                                       r.Stages?.Count ?? 1, domain, r.Constraints.RiskLevel, profiledBy);

        // ── 層 2：策略規則表（順序即優先級）──
        Strategy strategy;
        if (r.Reflexive)                                   { strategy = Strategy.InformationProcessing; why.Add("反身系統 → 不做方向預測"); }
        else if (c.Profile.StageCount > 1)                  { strategy = Strategy.MultiDomainPipeline;   why.Add("多段跨領域 → 流水線"); }
        else if (r.Probabilistic)                           { strategy = Strategy.ProbabilisticEnsemble; why.Add("機率問題 → 校準集成"); }
        else if (bestAvailable <= VerifierLevel.L1_LlmCritic && !hasUtilities)
                                                            { strategy = Strategy.PerspectiveDebate;     why.Add("無外部驗證器、無效用矩陣 → 只做觀點覆蓋"); }
        else if (bestAvailable >= VerifierLevel.L3_ExecutableTest && r.Constraints.RiskLevel == RiskLevel.Low)
                                                            { strategy = Strategy.SingleAgent;           why.Add("有機器驗證器且低風險 → 單一 agent + 驗證就夠（不為多 agent 而多 agent）"); }
        else if (bestAvailable >= VerifierLevel.L3_ExecutableTest)
                                                            { strategy = Strategy.SolverCriticVerifier;  why.Add("有機器/實驗驗證器 → solver-critic-verifier"); }
        else                                                { strategy = Strategy.GenerateTournament;    why.Add("只有 L1/L2 驗證 → 多產生 + 評審初篩 + 人類終選"); }

        // ── 層 3：歷史政策。Registry 有足夠證據才調整；證據不足就維持預設 ──
        int solverCount = strategy switch
        {
            Strategy.SingleAgent => 1,
            _ => r.Constraints.RiskLevel == RiskLevel.High ? 3 : 2
        };
        bool usesSolvers = strategy is not (Strategy.InformationProcessing or Strategy.PerspectiveDebate);
        var topSolver = usesSolvers ? _registry.Select("solver", domain, 1).FirstOrDefault() : null;
        if (topSolver is not null && solverCount > 1)
        {
            var st = _registry.Stats(topSolver.AgentId, domain, "solver");
            if (st.N >= 5 && st.Mean >= 0.85) { solverCount--; why.Add($"歷史：{topSolver.AgentId} 在 {domain} 上 {st.Mean:P0}（n={st.N}）→ 少用一個 solver"); }
            else why.Add($"歷史資料不足（最佳 solver n={st.N}）→ 維持 {solverCount} 個 solver");
        }

        bool requireHuman = r.Constraints.RiskLevel != RiskLevel.Low || bestAvailable <= VerifierLevel.L2_Rule;
        string wfName = strategy switch
        {
            Strategy.SingleAgent or Strategy.SolverVerifier => "solver_verifier",
            Strategy.SolverCriticVerifier   => "engineering_root_cause",
            Strategy.InformationProcessing  => "information_processing",
            Strategy.PerspectiveDebate      => "perspective_debate",
            Strategy.ProbabilisticEnsemble  => "probabilistic_ensemble",
            Strategy.GenerateTournament     => "generate_tournament",
            Strategy.MultiDomainPipeline    => "multi_domain_pipeline",
            _ => throw new NotSupportedException(strategy.ToString())
        };

        c.Plan = new WorkflowPlan(strategy, wfName, solverCount, requireHuman, RequireExternalEvidence: true, why);
        c.Add($"[router] 策略={strategy} workflow={wfName} solvers={solverCount} 人類核准={requireHuman}");
        foreach (var w in why) c.Add("[router]   · " + w);

        return strategy == Strategy.MultiDomainPipeline ? WorkflowCatalog.BuildPipeline(r.Stages!) : _workflows[wfName];
    }
}

/// <summary>流程目錄：流程是資料。新增一種解法 = 加一段 JSON，不改引擎。</summary>
public static class WorkflowCatalog
{
    public static IReadOnlyDictionary<string, WorkflowDefinition> Default() => new[]
    {
        // Solver ×N（平行）→ 規則 + 批判（平行）→ 先驗 → 實驗設計 → 人類核准 → 實驗 → 後驗 → 決策
        """
        {"name":"engineering_root_cause","steps":[
          {"id":"solvers",    "type":"parallel_agents","params":{"role":"solver"}},
          {"id":"rules",      "type":"verify","dependsOn":["solvers"],"params":{"level":"2"}},
          {"id":"critic",     "type":"verify","dependsOn":["solvers"],"params":{"level":"1"}},
          {"id":"prior",      "type":"probability","dependsOn":["rules","critic"],"params":{"mode":"prior"}},
          {"id":"experiment", "type":"agent","dependsOn":["prior"],"params":{"role":"experiment_designer"}},
          {"id":"approve",    "type":"human_checkpoint","dependsOn":["experiment"],"params":{"what":"實驗計畫"}},
          {"id":"run_exp",    "type":"verify","dependsOn":["approve"],"params":{"level":"4"},"timeoutSeconds":600},
          {"id":"posterior",  "type":"probability","dependsOn":["run_exp"],"params":{"mode":"update"}},
          {"id":"decide",     "type":"decision","dependsOn":["posterior"]}
        ]}
        """,
        """
        {"name":"solver_verifier","steps":[
          {"id":"solvers","type":"parallel_agents","params":{"role":"solver"}},
          {"id":"rules",  "type":"verify","dependsOn":["solvers"],"params":{"level":"2"}},
          {"id":"test",   "type":"verify","dependsOn":["rules"],"params":{"level":"3"}},
          {"id":"prior",  "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
          {"id":"decide", "type":"decision","dependsOn":["prior"]}
        ]}
        """,
        """
        {"name":"information_processing","steps":[
          {"id":"analysts",  "type":"parallel_agents","params":{"role":"analyst","count":"3",
                              "views":"抽取事實，只列可查證的事件與數字|情境 A（樂觀）與其領先指標|情境 B（悲觀）與其領先指標"}},
          {"id":"risk",      "type":"agent","dependsOn":["analysts"],"params":{"role":"analyst","view":"風險官：任何情境下都會受傷的部位與退出條件"}},
          {"id":"synthesize","type":"synthesize","dependsOn":["risk"]}
        ]}
        """,
        """
        {"name":"perspective_debate","steps":[
          {"id":"seats","type":"parallel_agents","params":{"role":"analyst","count":"4",
                         "views":"效益主義：只看後果|義務論：只看規則與底線|最弱勢的一方：第一人稱|十年後回頭看的人"}},
          {"id":"synthesize","type":"synthesize","dependsOn":["seats"]}
        ]}
        """,
        """
        {"name":"probabilistic_ensemble","steps":[
          {"id":"forecasters","type":"parallel_agents","params":{"role":"solver","count":"4"}},
          {"id":"rules",      "type":"verify","dependsOn":["forecasters"],"params":{"level":"2"}},
          {"id":"prior",      "type":"probability","dependsOn":["rules"],"params":{"mode":"prior"}},
          {"id":"decide",     "type":"decision","dependsOn":["prior"]}
        ]}
        """,
        """
        {"name":"generate_tournament","steps":[
          {"id":"generators","type":"parallel_agents","params":{"role":"solver","count":"4"}},
          {"id":"rules",     "type":"verify","dependsOn":["generators"],"params":{"level":"2"}},
          {"id":"critic",    "type":"verify","dependsOn":["generators"],"params":{"level":"1"}},
          {"id":"approve",   "type":"human_checkpoint","dependsOn":["rules","critic"],"params":{"what":"候選清單"}}
        ]}
        """,
    }.Select(WorkflowDefinition.FromJson).ToDictionary(w => w.Name);

    /// <summary>跨領域流水線由 Stages 動態展開：每段 = agent → 該段等級的驗證 → 邊界檢查（人類閘）。</summary>
    public static WorkflowDefinition BuildPipeline(IReadOnlyList<StageSpec> stages)
    {
        var steps = new List<StepDef>();
        string? prev = null;
        for (int i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            string a = $"s{i}_agent", v = $"s{i}_verify", g = $"s{i}_gate";
            steps.Add(new StepDef(a, "agent", prev is null ? null : new[] { prev },
                new() { ["role"] = "solver", ["task"] = s.Task, ["contract"] = s.Contract }));
            steps.Add(new StepDef(v, "verify", new[] { a }, new() { ["level"] = ((int)s.Verifier).ToString() }));
            steps.Add(new StepDef(g, "human_checkpoint", new[] { v }, new() { ["what"] = $"{s.Name} 介面契約：{s.Contract}" }));
            prev = g;
        }
        return new WorkflowDefinition("multi_domain_pipeline", steps.ToArray());
    }
}
