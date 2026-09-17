// ============================================================================
//  流程目錄：流程是資料。
//  Rev2 新增 step type：claim_canonicalize（主張對映 Catalog）、
//  experiment_register（LLM 提名 → 確定性程式登記似然表與 hash）。
// ============================================================================

using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Routing;

public static class WorkflowCatalog
{
    public static IReadOnlyDictionary<string, WorkflowDefinition> Default() => new[]
    {
        // 高風險：solver ×N → 正規化 → 規則 ∥ 批判 → 可執行測試 → 先驗
        //         → designer 提名 → 確定性登記(hash) → 人類核准 → L4(hash 比對) → 後驗 → 決策
        """
        {"name":"engineering_root_cause","steps":[
          {"id":"solvers",    "type":"parallel_agents","params":{"role":"solver"}},
          {"id":"canon",      "type":"claim_canonicalize","dependsOn":["solvers"]},
          {"id":"rules",      "type":"verify","dependsOn":["canon"],"params":{"level":"2"}},
          {"id":"critic",     "type":"verify","dependsOn":["canon"],"params":{"level":"1"}},
          {"id":"test",       "type":"verify","dependsOn":["rules","critic"],"params":{"level":"3"}},
          {"id":"prior",      "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
          {"id":"nominate",   "type":"agent","dependsOn":["prior"],"params":{"role":"experiment_designer"}},
          {"id":"register",   "type":"experiment_register","dependsOn":["nominate"]},
          {"id":"approve",    "type":"human_checkpoint","dependsOn":["register"],"params":{"what":"實驗計畫"}},
          {"id":"run_exp",    "type":"verify","dependsOn":["approve"],"params":{"level":"4"},"timeout":{"seconds":600,"onTimeout":"FailClosed"}},
          {"id":"posterior",  "type":"probability","dependsOn":["run_exp"],"params":{"mode":"update"}},
          {"id":"decide",     "type":"decision","dependsOn":["posterior"]}
        ]}
        """,
        // 同上，但省略 L1 critic：有 L3+ 機器驗證器時 critic 的邊際價值極低
        """
        {"name":"engineering_root_cause_nocritic","steps":[
          {"id":"solvers",    "type":"parallel_agents","params":{"role":"solver"}},
          {"id":"canon",      "type":"claim_canonicalize","dependsOn":["solvers"]},
          {"id":"rules",      "type":"verify","dependsOn":["canon"],"params":{"level":"2"}},
          {"id":"test",       "type":"verify","dependsOn":["rules"],"params":{"level":"3"}},
          {"id":"prior",      "type":"probability","dependsOn":["test"],"params":{"mode":"prior"}},
          {"id":"nominate",   "type":"agent","dependsOn":["prior"],"params":{"role":"experiment_designer"}},
          {"id":"register",   "type":"experiment_register","dependsOn":["nominate"]},
          {"id":"approve",    "type":"human_checkpoint","dependsOn":["register"],"params":{"what":"實驗計畫"}},
          {"id":"run_exp",    "type":"verify","dependsOn":["approve"],"params":{"level":"4"},"timeout":{"seconds":600,"onTimeout":"FailClosed"}},
          {"id":"posterior",  "type":"probability","dependsOn":["run_exp"],"params":{"mode":"update"}},
          {"id":"decide",     "type":"decision","dependsOn":["posterior"]}
        ]}
        """,
        """
        {"name":"solver_verifier","steps":[
          {"id":"solvers","type":"parallel_agents","params":{"role":"solver"}},
          {"id":"canon",  "type":"claim_canonicalize","dependsOn":["solvers"]},
          {"id":"rules",  "type":"verify","dependsOn":["canon"],"params":{"level":"2"}},
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
