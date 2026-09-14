// ============================================================================
//  Domain — 系統的核心實體不是 ChatMessage，是 DecisionCase。
//  一次決策 = 一個 Case：輸入、證據、主張、Agent 執行、驗證、機率、決策、真實結果，全部保存。
//
//  兩個世界的邊界寫在型別裡：
//    Probabilistic Zone（LLM 產生）：Claim、Experiment 的似然表、Agent 自報信心
//    Deterministic Zone（程式計算）：Evidence 可靠度、VerifierLevel、Beliefs、DecisionResult、Outcome
// ============================================================================

namespace DecisionAI.Domain;

#region 驗證器等級 --------------------------------------------------------------

public enum VerifierLevel
{
    L1_LlmCritic      = 1,   // 另一個 LLM 說「看起來對」
    L2_Rule           = 2,   // 確定性規則：引用存在、格式、範圍
    L3_ExecutableTest = 3,   // 編譯 / 單元測試 / property test / 模擬器
    L4_Experiment     = 4,   // 真實系統上的實驗（HIL、staging、A/B）
    L5_RealOutcome    = 5    // 部署後的真實結果
}

public static class VerifierTrust
{
    /// <summary>信心上限由「最高已通過的驗證器等級」決定。LLM 自報信心不進入這裡。</summary>
    public static double Cap(VerifierLevel l) => l switch
    {
        VerifierLevel.L1_LlmCritic      => 0.60,
        VerifierLevel.L2_Rule           => 0.70,
        VerifierLevel.L3_ExecutableTest => 0.90,
        VerifierLevel.L4_Experiment     => 0.95,
        VerifierLevel.L5_RealOutcome    => 1.00,
        _ => 0.0
    };

    /// <summary>只有 L3 以上的結果能更新 Agent 權重。L1/L2 只記錄不學習：否則評審被討好會汙染權重。</summary>
    public static bool UpdatesWeights(VerifierLevel l) => l >= VerifierLevel.L3_ExecutableTest;
}

#endregion

#region 證據 ---------------------------------------------------------------------

public enum EvidenceSource { UserInput, SourceCode, RuntimeLog, Database, Web, Paper, Sensor, ExperimentResult, HistoricalOutcome }

/// <summary>證據 ≠ Agent 意見。可靠度由來源類型查表（確定性），不是 LLM 逐筆估計。</summary>
public sealed record Evidence(string Id, EvidenceSource Source, string Origin, string Content, DateTime Timestamp)
{
    public double Reliability => Source switch
    {
        EvidenceSource.ExperimentResult  => 0.98,
        EvidenceSource.HistoricalOutcome => 0.98,
        EvidenceSource.RuntimeLog        => 0.95,
        EvidenceSource.Sensor            => 0.95,
        EvidenceSource.SourceCode        => 0.95,
        EvidenceSource.Database          => 0.90,
        EvidenceSource.UserInput         => 0.80,
        EvidenceSource.Paper             => 0.75,
        EvidenceSource.Web               => 0.55,
        _ => 0.50
    };
}

#endregion

#region 主張、實驗 -----------------------------------------------------------------

public enum ClaimKind { Hypothesis, Candidate, Forecast }

public sealed record Proposal(string AgentId, double StatedConfidence);

/// <summary>Claim：Agent 提出的東西（假設 / 候選方案 / 預測）。同一句話被多個 Agent 提出時合併成一個 Claim。</summary>
public sealed class Claim
{
    public required string Id { get; init; }
    public required ClaimKind Kind { get; init; }
    public required string Statement { get; init; }
    public List<Proposal> Proposals { get; } = new();
    public HashSet<string> EvidenceFor { get; } = new();
    public HashSet<string> EvidenceAgainst { get; } = new();
    public bool IsOpinion { get; set; }   // L2 規則：一條證據都沒引用 → 標記為意見，不進機率引擎

    public static string Normalize(string s)
        => string.Join(" ", s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// 實驗：預先登記（pre-registration）。似然表 P(outcome | claim) 在跑實驗「之前」寫下，
/// 之後 Bayesian 更新只能用登記過的欄；這是防事後解釋（hindsight）的機制。
/// </summary>
public sealed class Experiment
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required string[] Outcomes { get; init; }
    public required Dictionary<string, double[]> LikelihoodByClaim { get; init; }
    public required string DesignedBy { get; init; }
    public int? ObservedOutcome { get; set; }
}

#endregion

#region 請求、規劃、決策、結果 ------------------------------------------------------

public enum RiskLevel { Low, Medium, High }

public sealed record Constraints(double Budget, int MaxLatencySeconds, RiskLevel RiskLevel);

/// <summary>行動選項：效用矩陣是「人 / 政策表」給的確定性輸入；LLM 可以提案文字，不能填數字。</summary>
public sealed record ActionOption(string Id, string Description, IReadOnlyDictionary<string, double> UtilityByScenario);

public sealed record RiskPolicy(double MaxAcceptableLoss, double RobustnessDelta = 0.10, int RobustnessSamples = 500);

public sealed record StageSpec(string Name, string Task, VerifierLevel Verifier, string Contract);

public sealed record DecisionRequest(
    string Problem,
    string Goal,
    string Domain,                       // 空字串 → Router 用 LLM profiler 補（封閉標籤集，且記錄為可評估的預測）
    Constraints Constraints,
    bool Reflexive = false,              // 判準 4：預測會改變系統 → 禁止方向預測
    bool Probabilistic = false,
    IReadOnlyList<StageSpec>? Stages = null,
    IReadOnlyList<ActionOption>? Actions = null,
    RiskPolicy? RiskPolicy = null);

public enum Strategy
{
    SingleAgent, SolverVerifier, SolverCriticVerifier, PerspectiveDebate,
    ProbabilisticEnsemble, GenerateTournament, MultiDomainPipeline, InformationProcessing
}

/// <summary>ProblemProfile 是「事實 + 類別」，不是 8 個小數。驗證器等級是附了什麼，不是估計出來的。</summary>
public sealed record ProblemProfile(
    VerifierLevel BestAvailableVerifier,
    bool Reflexive,
    bool Probabilistic,
    bool HasUtilities,
    int StageCount,
    string Domain,
    RiskLevel Risk,
    string ProfiledBy);

public sealed record WorkflowPlan(
    Strategy Strategy,
    string WorkflowName,
    int SolverCount,
    bool RequireHumanApproval,
    bool RequireExternalEvidence,
    IReadOnlyList<string> Rationale);

public sealed record AgentRun(string AgentId, string Role, string RawOutput, double Cost, TimeSpan Elapsed, bool Ok, string? Error);

public sealed record VerificationResult(string VerifierId, VerifierLevel Level, string? ClaimId, bool? Pass, double Score, string Note);

public sealed record ActionMetrics(double ExpectedUtility, double WorstCase, double MaxRegret, bool Admissible);

public sealed record DecisionResult(
    string? RecommendedAction,
    double ExpectedUtility,
    double WorstCase,
    double MaxRegret,
    double Robustness,
    double Confidence,
    string Reason,
    IReadOnlyDictionary<string, ActionMetrics> PerAction);

public sealed record Outcome(string? TrueClaimId, IReadOnlyDictionary<string, bool> ClaimTruth, VerifierLevel Level, string Note, DateTime At);

#endregion

#region DecisionCase -------------------------------------------------------------------

public sealed class DecisionCase
{
    private static int _seq;

    public string Id { get; } = $"CASE-{Interlocked.Increment(ref _seq):000}";
    public DecisionRequest Request { get; }
    public ProblemProfile? Profile { get; set; }
    public WorkflowPlan? Plan { get; set; }

    public List<Evidence> Evidence { get; } = new();
    public List<Claim> Claims { get; } = new();
    public List<Experiment> Experiments { get; } = new();
    public List<AgentRun> AgentRuns { get; } = new();
    public List<VerificationResult> Verifications { get; } = new();
    public Dictionary<string, double> Beliefs { get; } = new();     // claimId → P（確定性引擎維護）
    public DecisionResult? Decision { get; set; }
    public Outcome? Outcome { get; set; }
    public string? Memo { get; set; }                                // 反身 / 觀點類流程的產出：備忘，不是決策

    public double CostSpent { get; set; }
    public Dictionary<string, string> StepStatus { get; } = new();
    public List<string> Log { get; } = new();
    public bool Halted { get; private set; }
    public string? HaltReason { get; private set; }

    public DecisionCase(DecisionRequest request) => Request = request;

    public void Halt(string reason) { Halted = true; HaltReason = reason; Add($"[halt] {reason}"); }
    public void Add(string line) { lock (Log) Log.Add(line); }

    /// <summary>Goodhart 防護要用：這個 Case 裡誰當過什麼角色。</summary>
    public IEnumerable<string> AgentsUsedAs(string role) => AgentRuns.Where(r => r.Role == role).Select(r => r.AgentId).Distinct();

    /// <summary>最高「已通過」的驗證等級：信心上限的依據。</summary>
    public VerifierLevel BestPassedLevel()
        => Verifications.Where(v => v.Pass == true).Select(v => v.Level).DefaultIfEmpty(VerifierLevel.L1_LlmCritic).Max();

    public IEnumerable<Claim> Hypotheses => Claims.Where(c => c.Kind == ClaimKind.Hypothesis && !c.IsOpinion);
}

#endregion
