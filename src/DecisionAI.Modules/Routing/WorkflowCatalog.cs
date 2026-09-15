// ============================================================================
//  流程目錄：流程是資料。
// ============================================================================

using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Routing;

public static class WorkflowCatalog
{
    public static IReadOnlyDictionary<string, WorkflowDefinition> Default() => new[]
    {
        // Solver ×N（平行）→ 規則 ∥ 批判 → 可執行測試 → 先驗 → 實驗設計 → 人類核准 → 實驗 → 後驗 → 決策
        """
        {"name":"engineering_root_cause","steps":[
          {"id":"solvers",    "type":"parallel_agents","params":{"role":"solver"}},
          {"id":"rules",      "type":"verify","dependsOn":["solvers"],"params":{"level":"2"}},
          {"id":"critic",     "type":"verify","dependsOn":["solvers"],"params":{"level":"1"}},
          {"id":"test",       "type":"verify","dependsOn":["rules","critic"],"params":{"level":"3"}},
          {"id":"prior",      "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
          {"id":"experiment", "type":"agent","dependsOn":["prior"],"params":{"role":"experiment_designer"}},
          {"id":"approve",    "type":"human_checkpoint","dependsOn":["experiment"],"params":{"what":"實驗計畫"}},
          {"id":"run_exp",    "type":"verify","dependsOn":["approve"],"params":{"level":"4"},"timeout":{"seconds":600,"onTimeout":"FailClosed"}},
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
                new() { ["role"] = "stage_solver", ["stage"] = s.Name, ["task"] = s.Task, ["contract"] = s.Contract }));
            steps.Add(new StepDef(v, "verify", new[] { a }, new() { ["level"] = ((int)s.Verifier).ToString(), ["stage"] = s.Name }));
            steps.Add(new StepDef(g, "human_checkpoint", new[] { v }, new() { ["what"] = $"{s.Name} 介面契約：{s.Contract}" }));
            prev = g;
        }
        return new WorkflowDefinition("multi_domain_pipeline", steps.ToArray());
    }
}
