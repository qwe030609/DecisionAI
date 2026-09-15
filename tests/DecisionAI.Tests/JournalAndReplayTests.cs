// ============================================================================
//  寫入邊界（權限矩陣）、可重放、架構規則。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Modules.Journal;
using DecisionAI.Testing;
using System.Text.Json;
using Xunit;

namespace DecisionAI.Tests;

public class JournalPermissionTests
{
    private static CaseJournal NewJournal(IRolePermission? p = null) => new("CASE-P", p ?? new RolePermissionMatrix(), new FixedClock());

    private static ClaimProposed Hyp(string actorRole) =>
        new ClaimProposed("H1", ClaimKind.Hypothesis, "x", 0.8, ImmutableArray.Create("EV-001"), ImmutableArray<string>.Empty)
        { StepId = "s", ActorId = "someone", ActorRole = actorRole };

    [Fact]
    public void Critic_CannotProposeClaims_StructuralGoodhartGuard()
    {
        var j = NewJournal();
        var r = j.Apply(Hyp(Actors.Critic));
        Assert.False(r.Accepted);
        Assert.Contains("critic", r.Reason);
        Assert.Empty(j.State.Claims);
        Assert.Empty(j.Events);
    }

    [Fact]
    public void Solver_CanProposeClaims_ButCannotWriteVerification()
    {
        var j = NewJournal();
        Assert.True(j.Apply(Hyp(Actors.Solver)).Accepted);
        var v = new VerificationRecorded(new("me", VerifierLevel.L3_ExecutableTest, "H1", true, 1, "自證"))
                { StepId = "s", ActorId = "solver-A", ActorRole = Actors.Solver };
        Assert.False(j.Apply(v).Accepted);
        Assert.Equal(VerifierLevel.None, j.State.BestPassedLevel());
    }

    [Theory]
    [InlineData(Actors.Solver)]
    [InlineData(Actors.Critic)]
    [InlineData(Actors.ExperimentDesigner)]
    [InlineData(Actors.Analyst)]
    [InlineData(Actors.System)]
    public void NoLlmRoleAndNotEvenSystem_CanWriteUtilityMatrix(string role)
    {
        var j = NewJournal();
        var e = new UtilityMatrixSet(AtsScenario.Request().ActionsOrEmpty, new RiskPolicy(50)) { StepId = "s", ActorId = "x", ActorRole = role };
        Assert.False(j.Apply(e).Accepted);
        Assert.Empty(j.State.Utilities);
    }

    [Fact]
    public void Human_CanWriteUtilityMatrix()
    {
        var j = NewJournal();
        var e = new UtilityMatrixSet(AtsScenario.Request().ActionsOrEmpty, new RiskPolicy(50)) { StepId = "s", ActorId = "owner", ActorRole = Actors.Human };
        Assert.True(j.Apply(e).Accepted);
        Assert.Equal(3, j.State.Utilities.Length);
    }

    [Fact]
    public void Critic_CanOnlyWriteL1_NotHigherLevels()
    {
        var j = NewJournal();
        j.ApplyOrThrow(Hyp(Actors.Solver));
        var l1 = new VerificationRecorded(new("llm-critic:D", VerifierLevel.L1_LlmCritic, "H1", true, 0.8, "")) { StepId = "s", ActorId = "critic-D", ActorRole = Actors.Critic };
        var l3 = new VerificationRecorded(new("llm-critic:D", VerifierLevel.L3_ExecutableTest, "H1", true, 1, "")) { StepId = "s", ActorId = "critic-D", ActorRole = Actors.Critic };
        Assert.True(j.Apply(l1).Accepted);
        Assert.False(j.Apply(l3).Accepted);
        Assert.Equal(VerifierLevel.L1_LlmCritic, j.State.BestPassedLevel());
    }

    [Fact]
    public void UnstampedEvent_IsRejected_FailClosed()
    {
        var j = NewJournal();
        var r = j.Apply(new Noted("hi"));   // 沒有 StepId / ActorRole
        Assert.False(r.Accepted);
    }

    [Fact]
    public void MutationSwitch_AlwaysAllow_MakesTheGuardTestRed()
    {
        // 這條測試記錄「壞的守門件會讓什麼通過」；真正的 mutation 測試是把 AlwaysAllow 注入整個系統，再看上面的測試是否變紅。
        var j = NewJournal(new AlwaysAllowPermission());
        Assert.True(j.Apply(Hyp(Actors.Critic)).Accepted);
    }
}

public class ReplayTests
{
    [Fact]
    public async Task SameInputs_ProduceIdenticalEventStreams()
    {
        var a = await TestSystem.Build().Orchestrator.RunAsync("CASE-R", AtsScenario.Request(), AtsScenario.Evidence());
        var b = await TestSystem.Build().Orchestrator.RunAsync("CASE-R", AtsScenario.Request(), AtsScenario.Evidence());

        Assert.Equal(a.Journal.Events.Count, b.Journal.Events.Count);
        foreach (var (x, y) in a.Journal.Events.Zip(b.Journal.Events))
        {
            Assert.Equal(x.GetType(), y.GetType());
            Assert.Equal(x.Seq, y.Seq);
            Assert.Equal(x.StepId, y.StepId);
            Assert.Equal(x.ActorId, y.ActorId);
            Assert.Equal(x.At, y.At);
        }
        Assert.Equal(a.State.Log, b.State.Log);
        Assert.Equal(a.State.Beliefs, b.State.Beliefs);
        Assert.Equal(Json(a.State.Decision), Json(b.State.Decision));       // record 內含不可變集合 → 以結構化序列化比較
    }

    [Fact]
    public async Task Fold_OfEvents_EqualsIncrementalState()
    {
        var run = await TestSystem.Build().Orchestrator.RunAsync("CASE-F", AtsScenario.Request(), AtsScenario.Evidence());
        var folded = CaseReducer.Fold("CASE-F", run.Journal.Events);
        Assert.Equal(run.State.Log, folded.Log);
        Assert.Equal(Json(run.State.Claims), Json(folded.Claims));
        Assert.Equal(run.State.Beliefs, folded.Beliefs);
        Assert.Equal(Json(run.State.Decision), Json(folded.Decision));
        Assert.Equal(run.State.StepStatus, folded.StepStatus);
        Assert.Equal(run.State.CostSpent, folded.CostSpent);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task ParallelSteps_AreWrittenInDeclarationOrder_NotCompletionOrder()
    {
        var run = await TestSystem.Build().Orchestrator.RunAsync("CASE-O", AtsScenario.Request(), AtsScenario.Evidence());
        var steps = run.Journal.Events.Where(e => e.StepId is "rules" or "critic").Select(e => e.StepId).ToList();
        int lastRules = steps.LastIndexOf("rules"), firstCritic = steps.IndexOf("critic");
        Assert.True(lastRules < firstCritic, "rules 的所有事件必須排在 critic 之前（宣告順序）");
    }
}

public class ArchitectureTests
{
    /// <summary>確定性區在編譯期就碰不到 LLM：Probability / Decision / Assurance 資料夾不得出現 ILlm。</summary>
    [Theory]
    [InlineData("Probability")]
    [InlineData("Decision")]
    [InlineData("Assurance")]
    public void DeterministicZone_NeverReferencesILlm(string folder)
    {
        var dir = FindModulesFolder(folder);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ILlm", text.Replace("不得出現 ILlm", ""));
            Assert.DoesNotContain("Modules.Agents", text);
        }
    }

    private static string FindModulesFolder(string folder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DecisionAI.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "DecisionAI.Modules", folder);
        Assert.True(Directory.Exists(path), path);
        return path;
    }
}
