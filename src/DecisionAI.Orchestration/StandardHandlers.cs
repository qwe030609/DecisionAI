// ============================================================================
//  Handlers — 把 workflow 的 step type 接到各模組。這裡就是 Intake 邊界：
//  LLM 輸出 → schema 解析 → 以「agent 的角色」建事件 → Journal 的權限矩陣決定收不收。
//  handler 只讀快照、回傳事件；不直接寫 Journal。
//
//  Rev2 新增兩個 step type：
//    claim_canonicalize   主張對映 Catalog；灰帶升級 arbiter，不自動猜
//    experiment_register  designer 只提名，登記（似然定案 + hash）由確定性程式做
// ============================================================================

using System.Collections.Immutable;
using System.Text;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Catalog;
using DecisionAI.Modules.Decision;
using DecisionAI.Modules.Probability;
using DecisionAI.Modules.Verification;
using DecisionAI.Modules.Workflow;

namespace DecisionAI.Orchestration;

public sealed class StandardHandlers
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly VerifierRegistry _verifiers;
    private readonly IEnsembler _ensembler;
    private readonly IBayesUpdater _bayes;
    private readonly IDecisionEngine _decision;
    private readonly IHumanGateway _human;
    private readonly IInjectionGuard _guard;
    private readonly IRandomSource _rng;
    private readonly IPolicyStore _policy;
    private readonly IClaimCatalog _catalog;
    private readonly IClaimCanonicalizer _canonicalizer;
    private readonly ILikelihoodElicitor _elicitor;

    public StandardHandlers(IAgentRegistry registry, AgentRunner runner, VerifierRegistry verifiers, IEnsembler ensembler,
                            IBayesUpdater bayes, IDecisionEngine decision, IHumanGateway human, IInjectionGuard guard,
                            IRandomSource rng, IPolicyStore policy, IClaimCatalog catalog,
                            IClaimCanonicalizer canonicalizer, ILikelihoodElicitor elicitor)
    {
        _registry = registry; _runner = runner; _verifiers = verifiers; _ensembler = ensembler; _bayes = bayes;
        _decision = decision; _human = human; _guard = guard; _rng = rng; _policy = policy;
        _catalog = catalog; _canonicalizer = canonicalizer; _elicitor = elicitor;
    }

    public void RegisterAll(WorkflowEngine engine)
    {
        engine.RegisterHandler("parallel_agents",     ParallelAgentsAsync);
        engine.RegisterHandler("agent",               SingleAgentAsync);
        engine.RegisterHandler("claim_canonicalize",  CanonicalizeAsync);
        engine.RegisterHandler("experiment_register", RegisterExperimentsAsync);
        engine.RegisterHandler("verify",              VerifyAsync);
        engine.RegisterHandler("probability",         ProbabilityAsync);
        engine.RegisterHandler("decision",            DecisionAsync);
        engine.RegisterHandler("human_checkpoint",    HumanCheckpointAsync);
    }

    private static CaseEvent Sys(string stepId, CaseEvent e) => e.By(stepId, "handler", Actors.System);

    // ── parallel_agents：同一角色 N 個 agent 平行 I/O；吸收時依「指派順序」而非完成順序 ──
    private async Task<IReadOnlyList<CaseEvent>> ParallelAgentsAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string role = step.P("role", "solver");
        var assigned = s.Roles.Where(r => r.Role == role).Select(r => r.AgentId).ToList();
        if (assigned.Count == 0) throw new InvalidOperationException($"角色指派裡沒有 {role}（domain={s.Domain}）");

        var agents = assigned.Select(_registry.Get).ToList();
        var events = new List<CaseEvent> { Sys(step.Id, new Noted($"依角色指派使用 {string.Join(", ", assigned)}")) };
        var (user, flagged) = UserPrompt(s, step);
        if (flagged.Length > 0) events.Add(Sys(step.Id, new Noted($"InjectionGuard 標記可疑證據：{string.Join(",", flagged)}")));

        var runs = await Task.WhenAll(agents.Select((a, i) =>
            _runner.RunAsync(a, new LlmCallKey(s.Id, step.Id, a.Spec.AgentId, 0), role, SystemPrompt(role), user,
                             role == "solver" ? 0.4 + 0.3 * i / Math.Max(1, agents.Count - 1) : 0.7, ct)));

        var working = new List<Claim>(s.Claims);
        foreach (var run in runs) events.AddRange(Absorb(step, s, run, role, working));
        return events;
    }

    private async Task<IReadOnlyList<CaseEvent>> SingleAgentAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string role = step.P("role", "solver");
        string registryRole = role == Actors.StageSolver ? "solver" : role;
        var assigned = s.Roles.FirstOrDefault(r => r.Role == role || r.Role == registryRole);
        var agent = assigned is not null ? _registry.Get(assigned.AgentId)
                    : _registry.Select(new SelectionQuery(registryRole, s.Domain, 1), _policy.Pin(s.PolicyVersion)).FirstOrDefault()
                      ?? throw new InvalidOperationException($"沒有可用的 {registryRole} agent");

        var (user, flagged) = UserPrompt(s, step);
        var events = new List<CaseEvent>();
        if (flagged.Length > 0) events.Add(Sys(step.Id, new Noted($"InjectionGuard 標記可疑證據：{string.Join(",", flagged)}")));
        var run = await _runner.RunAsync(agent, new LlmCallKey(s.Id, step.Id, agent.Spec.AgentId, 0), role, SystemPrompt(role), user, 0.5, ct);
        events.AddRange(Absorb(step, s, run, role, new List<Claim>(s.Claims)));
        return events;
    }

    // ── claim_canonicalize：對映 Catalog。灰帶升級 arbiter，不自動猜 ──
    private async Task<IReadOnlyList<CaseEvent>> CanonicalizeAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var view = _catalog.View(s.ClaimCatalogVersion);
        var events = new List<CaseEvent>();

        // 對「每一個被提出的 frame」各做一次對映——不是只對合併後的第一個。
        // 這樣「同四元組不同措辭是否得到同一個 canonical id」才是可稽核的（MR-14）。
        foreach (var proposed in ProposedFrames(s))
        {
            var r = _canonicalizer.Canonicalize(proposed.Frame, view, s.Domain);
            string catalogId = r.CatalogId;
            var kind = r.Kind;

            if (r.Kind == MatchKind.Ambiguous)
            {
                var verdict = await _human.RequestAsync(new HumanRequest(s.Id, step.Id, HumanRole.Arbiter,
                    $"{proposed.LocalId} 的對映落在灰帶：{proposed.Frame.Identity}",
                    r.Candidates.Insert(0, "候選 catalog 條目：").ToImmutableArray()), ct);
                events.Add(new HumanActed(HumanRole.Arbiter.ToString(),
                    $"{proposed.LocalId} 對映灰帶", verdict.Approved, verdict.Rationale).By(step.Id, "arbiter", Actors.Human));
                catalogId = verdict.Approved ? r.Candidates[0] : proposed.Frame.DraftId;
                kind = verdict.Approved ? MatchKind.Fuzzy : MatchKind.New;
            }

            events.Add(Sys(step.Id, new ClaimCanonicalized(proposed.LocalId, catalogId, kind, r.MatchScore,
                                                           WasNewEntry: kind == MatchKind.New)));
        }
        return events;
    }

    /// <summary>逐筆（不是每個合併後的主張一筆）：同四元組不同措辭是否得到同一個 canonical id 才可稽核。</summary>
    private static IEnumerable<(string LocalId, ClaimFrame Frame)> ProposedFrames(CaseState s)
    {
        var hyp = s.Claims.Where(c => c.Kind == ClaimKind.Hypothesis).Select(c => c.LocalId).ToHashSet();
        return s.ProposedFrames.Where(p => hyp.Contains(p.LocalId));
    }

    // ── experiment_register：LLM 只提名；似然定案與 hash 由確定性程式做 ──
    private Task<IReadOnlyList<CaseEvent>> RegisterExperimentsAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var events = new List<CaseEvent>();
        var known = s.Claims.Where(c => c.Kind == ClaimKind.Hypothesis && !c.IsOpinion).Select(c => c.LocalId).ToHashSet();
        int n = s.Experiments.Count;

        foreach (var draft in s.Nominations)
        {
            var (exp, skip) = _elicitor.Register(draft, $"EXP-{n + 1}", known);
            if (exp is null)
            {
                events.Add(Sys(step.Id, new ExperimentSkipped(draft.DraftId, 0, skip ?? "無法登記")));
                continue;
            }
            n++;
            events.Add(Sys(step.Id, new ExperimentPreRegistered(exp)));
            events.Add(Sys(step.Id, new Noted(
                $"{exp.Id} 登記（來源 {exp.LikelihoodSource}，hash {exp.LikelihoodHash[..8]}…）：{exp.Description}；" +
                string.Join(" ", exp.LikertByClaim.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value.Select(Likert.Label))}]")))));
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }

    private async Task<IReadOnlyList<CaseEvent>> VerifyAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var level = (VerifierLevel)step.PInt("level", 2);
        var vs = _verifiers.AtLevel(level);
        if (vs.Count == 0) return new[] { Sys(step.Id, new Noted($"沒有註冊 {level} 驗證器 → 略過（能力層上限不會提升）")) };

        var events = new List<CaseEvent>();
        foreach (var v in vs)
        {
            var produced = await v.VerifyAsync(s, step.Id, ct);
            events.AddRange(produced);
            var results = produced.OfType<VerificationRecorded>().Select(r => r.Result).ToList();
            events.Add(Sys(step.Id, new Noted($"{v.Id}（{level}）：{results.Count(r => r.Pass == true)} 通過 / {results.Count(r => r.Pass == false)} 未過")));
            foreach (var r in results.Where(r => r.Pass == false || r.Level >= VerifierLevel.L3_ExecutableTest))
                events.Add(Sys(step.Id, new Noted($"  · {r.ClaimId ?? "-"}: {r.Note}")));
        }
        return events;
    }

    private Task<IReadOnlyList<CaseEvent>> ProbabilityAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        var events = new List<CaseEvent>();
        if (step.P("mode", "prior") == "prior")
        {
            var prior = _ensembler.Prior(s, _policy.Pin(s.PolicyVersion));
            if (prior.Count == 0) events.Add(Sys(step.Id, new Noted("沒有可進機率引擎的假設（全是意見或無主張）")));
            else events.Add(Sys(step.Id, new BeliefUpdated(prior, "先驗（集成）：" + Beliefs.Render(prior))));
        }
        else
        {
            var b = s.Beliefs;
            foreach (var exp in s.Experiments.Where(e => e.ObservedOutcome is not null))
            {
                b = _bayes.Update(b, exp);
                events.Add(Sys(step.Id, new BeliefUpdated(b, $"{exp.Id} 觀察 outcome[{exp.ObservedOutcome}] → 後驗：{Beliefs.Render(b)}")));
            }
        }
        return Task.FromResult<IReadOnlyList<CaseEvent>>(events);
    }

    private Task<IReadOnlyList<CaseEvent>> DecisionAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        if (s.Utilities.Length == 0 || s.Beliefs.Count == 0)
            return Task.FromResult<IReadOnlyList<CaseEvent>>(new[] { Sys(step.Id, new Noted("沒有效用矩陣或機率分布 → 不算 EU，交人決定")) });

        var result = _decision.Decide(s.Beliefs, s.Utilities, s.RiskPolicy ?? new RiskPolicy(double.PositiveInfinity), _rng.Fork(s.Id));
        return Task.FromResult<IReadOnlyList<CaseEvent>>(new[]
        {
            Sys(step.Id, new DecisionMade(result)),
            Sys(step.Id, new Noted($"建議 {result.RecommendedAction ?? "（無）"}：{result.Reason}"))
        });
    }

    private async Task<IReadOnlyList<CaseEvent>> HumanCheckpointAsync(StepDef step, CaseState s, CancellationToken ct)
    {
        string what = step.P("what", step.Id);
        if (s.Plan is { RequireHumanApproval: false })
            return new[] { Sys(step.Id, new Noted($"計畫不要求人類核准 → 通過（{what}）")) };

        // 呈現順序：證據與最壞情況先、系統建議後（automation bias 防護的最小版）
        var context = ImmutableArray.CreateBuilder<string>();
        context.Add($"問題：{s.Request!.Problem}");
        context.Add($"證據 {s.Evidence.Count} 條；主張 {s.Claims.Count} 條；已通過最高驗證等級 {s.BestPassedLevel()}");
        if (s.Decision is { } d) context.Add($"最壞情況 {d.WorstCase:F0}、最大後悔 {d.MaxRegret:F0}");
        foreach (var e in s.Experiments.Where(e => e.ObservedOutcome is null))
            context.Add($"待跑實驗 {e.Id}：{e.Description}（似然已登記，hash {e.LikelihoodHash[..8]}…）");

        var verdict = await _human.RequestAsync(new HumanRequest(s.Id, step.Id, HumanRole.Approver, what, context.ToImmutable()), ct);
        var events = new List<CaseEvent>
        {
            new HumanActed(HumanRole.Approver.ToString(), what, verdict.Approved, verdict.Rationale).By(step.Id, "approver", Actors.Human)
        };
        if (!verdict.Approved) events.Add(Sys(step.Id, new Halted($"人類拒絕：{what}", ImmutableArray<string>.Empty)));
        return events;
    }

    // ── 提示組裝：證據只進 user prompt，且經 InjectionGuard 中性化 ──
    private static string SystemPrompt(string role) => role switch
    {
        "solver"              => RolePrompts.Solver,
        "experiment_designer" => RolePrompts.ExperimentDesigner,
        Actors.StageSolver    => RolePrompts.StageSolver,
        _                     => throw new InvalidOperationException($"未知的 agent 角色 {role}（fail-closed）")
    };

    private (string User, ImmutableArray<string> Flagged) UserPrompt(CaseState s, StepDef step)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"問題：{s.Request!.Problem}");
        sb.AppendLine($"目標：{s.Request.Goal}");
        if (step.P("task").Length > 0) sb.AppendLine($"本段任務：{step.P("task")}\n輸出契約：{step.P("contract")}");
        var flagged = ImmutableArray<string>.Empty;
        if (s.Evidence.Count > 0)
        {
            var r = _guard.Render(s.Evidence);
            sb.AppendLine().AppendLine(r.Text);
            flagged = r.FlaggedEvidenceIds;
        }
        if (s.Claims.Any(k => k.Kind == ClaimKind.Hypothesis))
            sb.AppendLine("\n目前假設：\n" + string.Join("\n", s.Hypotheses.Select(h =>
                $"{h.LocalId}: {h.Frame.Render()}" + (s.Beliefs.TryGetValue(h.LocalId, out var p) ? $"（P={p:P0}）" : ""))));
        return (sb.ToString(), flagged);
    }

    // ── 吸收 agent 輸出：schema 驗證 → 以 agent 的角色建事件 ──
    private static IEnumerable<CaseEvent> Absorb(StepDef step, CaseState s, AgentRun run, string role, List<Claim> working)
    {
        yield return Sys(step.Id, new AgentRunRecorded(run));
        if (!run.Ok) { yield return Sys(step.Id, new Noted($"{run.AgentId} 失敗：{run.Error}")); yield break; }

        switch (role)
        {
            case "solver":
                var hyps = AgentOutputs.Hypotheses(run.RawOutput);
                if (hyps.Count == 0) yield return Sys(step.Id, new Noted($"{run.AgentId} 輸出無法解析（L2 會視為無主張）"));
                foreach (var h in hyps)
                {
                    // schema 是 Intake 邊界的一半：四元組不完整就擋在外面，不進 Journal
                    if (h.Frame.SchemaError is { } err)
                    { yield return Sys(step.Id, new ClaimRejected($"{run.AgentId}：{err}", h.Frame)); continue; }

                    string id = ResolveLocalId(working, ClaimKind.Hypothesis, h.Frame);
                    yield return new ClaimProposed(id, new ClaimKey(h.Frame.DraftId), ClaimKind.Hypothesis, h.Frame,
                                                   h.Confidence, h.EvidenceFor, h.EvidenceAgainst)
                        .By(step.Id, run.AgentId, Actors.Solver);
                }
                break;

            case "experiment_designer":
                foreach (var e in AgentOutputs.Experiments(run.RawOutput))
                {
                    var draft = new ExperimentDraft(
                        "NOM-" + ClaimFrame.Hash(ClaimFrame.Norm(e.Description))[..8],
                        e.Description, e.Outcomes, e.Votes, run.AgentId, e.Cost);
                    yield return new ExperimentNominated(draft).By(step.Id, run.AgentId, Actors.ExperimentDesigner);
                }
                break;

            case Actors.StageSolver:
                var frame = new ClaimFrame(Mechanisms.StageDeliverable, step.P("stage", step.Id),
                                           step.P("task", "段落任務"), step.P("contract", "輸出契約"));
                string cid = ResolveLocalId(working, ClaimKind.Candidate, frame);
                yield return new ClaimProposed(cid, new ClaimKey(frame.DraftId), ClaimKind.Candidate, frame,
                                               LikertBelief.EvenOdds, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)
                    .By(step.Id, run.AgentId, Actors.StageSolver);
                break;

            default:
                throw new InvalidOperationException($"沒有 {role} 的吸收規則（fail-closed）");
        }
    }

    /// <summary>
    /// 本地合併鍵是四元組的 Mechanism|Locus，不是自然語言。
    /// 同一個故障被兩個 agent 用不同措辭描述 Trigger / Observable，仍然合併成同一個 LocalId。
    /// </summary>
    private static string ResolveLocalId(List<Claim> working, ClaimKind kind, ClaimFrame frame)
    {
        var existing = working.FirstOrDefault(k => k.Kind == kind && k.Frame.Identity == frame.Identity);
        if (existing is not null) return existing.LocalId;

        string id = $"{(kind == ClaimKind.Hypothesis ? "H" : "C")}{working.Count(k => k.Kind == kind) + 1}";
        working.Add(new Claim(id, new ClaimKey(frame.DraftId), kind, frame, ImmutableList<Proposal>.Empty,
                              ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty, false, MatchKind.New, 0));
        return id;
    }
}
