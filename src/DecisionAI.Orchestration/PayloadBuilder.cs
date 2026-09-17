// ============================================================================
//  拒答時的替代交付（Rev2 §4.10 / Q5）
//  拒絕的是「帶信心值的方向性答案」，不是拒絕提供幫助。
//  三種需要生成的 payload 由 briefer agent 產出，然後一律過 L2：
//  引用必須存在、領先指標必須可觀察、各情境必須對稱。不過就退回，不輸出。
// ============================================================================

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Journal;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Agents;
using DecisionAI.Modules.Assurance;

namespace DecisionAI.Orchestration;

public sealed record PayloadOutcome(SubstitutePayload? Payload, ImmutableArray<CaseEvent> Events);

public sealed class PayloadBuilder
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunner _runner;
    private readonly IInjectionGuard _guard;
    private readonly IPayloadVerifier _verifier;
    private readonly IEvidenceStore _store;
    private readonly IPolicyStore _policy;

    public PayloadBuilder(IAgentRegistry registry, AgentRunner runner, IInjectionGuard guard,
                          IPayloadVerifier verifier, IEvidenceStore store, IPolicyStore policy)
    { _registry = registry; _runner = runner; _guard = guard; _verifier = verifier; _store = store; _policy = policy; }

    public async Task<PayloadOutcome> BuildAsync(CaseState s, ReasonCode code, CancellationToken ct)
    {
        const string stepId = "substitute";
        var events = ImmutableArray.CreateBuilder<CaseEvent>();

        // 不需要 LLM 的三種：直接由確定性程式組出來
        SubstitutePayload? payload = code switch
        {
            ReasonCode.NoVerifier => DeterministicPayloads.NoVerifier(s.Facts?.BestAvailableVerifier ?? VerifierLevel.None),
            ReasonCode.DelegatedToExternalModel when s.Request?.Specialist is { } sp
                => new DelegationHandoff(sp.ToolId, sp.WhyThisTool, sp.HowToRead, sp.UncertaintyNotes, sp.InputsRequired),
            _ => null
        };

        if (payload is null)
        {
            var (generated, genEvents) = await GenerateAsync(s, code, stepId, ct);
            events.AddRange(genEvents);
            payload = generated;
        }

        if (payload is null)
        {
            events.Add(new Noted($"無法產生 {code} 的替代交付 → 只輸出理由").By(stepId, "payload", Actors.System));
            return new PayloadOutcome(null, events.ToImmutable());
        }

        // payload 也要過 L2：它是 LLM 產生的，事實部分必須引用存在的證據
        var verdict = _verifier.Verify(payload, code, _store);
        foreach (var r in verdict.Results)
            events.Add(new VerificationRecorded(r).By(stepId, "payload-rule", Actors.Machine));

        if (!verdict.Ok)
        {
            events.Add(new Noted($"替代交付未通過 L2：{string.Join("；", verdict.Failures)} → 退回，不輸出")
                .By(stepId, "payload", Actors.System));
            return new PayloadOutcome(null, events.ToImmutable());
        }

        events.Add(new Noted($"替代交付 {payload.Kind} 通過 L2（{payload.EvidenceRefs.Length} 條證據引用）")
            .By(stepId, "payload", Actors.System));
        return new PayloadOutcome(payload, events.ToImmutable());
    }

    private async Task<(SubstitutePayload?, ImmutableArray<CaseEvent>)> GenerateAsync(
        CaseState s, ReasonCode code, string stepId, CancellationToken ct)
    {
        var events = ImmutableArray.CreateBuilder<CaseEvent>();
        string? prompt = code switch
        {
            ReasonCode.ReflexiveDirection  => RolePrompts.BrieferScenario,
            ReasonCode.NoGroundTruth       => RolePrompts.BrieferConsideration,
            ReasonCode.DefinitionalDispute => RolePrompts.BrieferDefinition,
            _ => null
        };
        if (prompt is null) return (null, events.ToImmutable());

        var briefer = _registry.Select(new SelectionQuery("briefer", s.Domain, 1), _policy.Pin(s.PolicyVersion)).FirstOrDefault();
        if (briefer is null)
        {
            events.Add(new Noted("沒有可用的 briefer agent → 無法產生替代交付").By(stepId, "payload", Actors.System));
            return (null, events.ToImmutable());
        }

        string user = $"問題：{s.Request!.Problem}\n目標：{s.Request.Goal}\n\n{_guard.Render(s.Evidence).Text}";
        var run = await _runner.RunAsync(briefer, new LlmCallKey(s.Id, stepId, briefer.Spec.AgentId, 0), "briefer", prompt, user, 0.3, ct);
        events.Add(new AgentRunRecorded(run).By(stepId, "runner", Actors.System));
        if (!run.Ok) return (null, events.ToImmutable());

        var node = AgentOutputs.ParseJson(run.RawOutput);
        if (node is null)
        {
            events.Add(new Noted("briefer 輸出無法解析").By(stepId, "payload", Actors.System));
            return (null, events.ToImmutable());
        }

        SubstitutePayload? payload = code switch
        {
            ReasonCode.ReflexiveDirection  => ParseBriefing(node),
            ReasonCode.NoGroundTruth       => ParseConsiderations(node),
            ReasonCode.DefinitionalDispute => ParseDefinitions(node),
            _ => null
        };
        return (payload, events.ToImmutable());
    }

    // ── 解析：全部是「型別化」的，缺欄位就留空讓 L2 抓 ──
    private static ImmutableArray<string> Refs(JsonNode? n)
        => n?.AsArray().Select(v => (string?)v ?? "").Where(x => x.Length > 0).ToImmutableArray() ?? ImmutableArray<string>.Empty;

    private static ImmutableArray<FactLine> Facts(JsonNode? n)
        => n?.AsArray().Select(f => new FactLine((string?)f!["text"] ?? "", Refs(f["evidenceFor"]))).ToImmutableArray()
           ?? ImmutableArray<FactLine>.Empty;

    private static ScenarioBriefing ParseBriefing(JsonNode node)
    {
        var scenarios = node["scenarios"]?.AsArray().Select(sc => new Scenario(
            (string?)sc!["name"] ?? "",
            Facts(sc["basis"]),
            sc["indicators"]?.AsArray().Select(i => new LeadingIndicator(
                (string?)i!["name"] ?? "", (string?)i["source"] ?? "",
                (string?)i["threshold"] ?? "", (string?)i["frequency"] ?? "")).ToImmutableArray()
                ?? ImmutableArray<LeadingIndicator>.Empty)).ToImmutableArray()
            ?? ImmutableArray<Scenario>.Empty;

        var risk = new RiskExit((string?)node["risk"]?["exposure"] ?? "", (string?)node["risk"]?["exitCondition"] ?? "");
        return new ScenarioBriefing(Facts(node["facts"]), scenarios, risk);
    }

    private static ConsiderationMap ParseConsiderations(JsonNode node) => new(
        node["items"]?.AsArray().Select(i => new Consideration(
            (string?)i!["name"] ?? "", (string?)i["whoItMattersTo"] ?? "", Refs(i["evidenceFor"]))).ToImmutableArray()
            ?? ImmutableArray<Consideration>.Empty,
        node["conflicts"]?.AsArray().Select(c => new Conflict(
            (string?)c!["between"] ?? "", (string?)c["nature"] ?? "")).ToImmutableArray()
            ?? ImmutableArray<Conflict>.Empty,
        node["forHumans"]?.AsArray().Select(q => new OpenQuestion(
            (string?)q!["question"] ?? "", (string?)q["whoDecides"] ?? "")).ToImmutableArray()
            ?? ImmutableArray<OpenQuestion>.Empty);

    private static DefinitionMap ParseDefinitions(JsonNode node) => new(
        node["definitions"]?.AsArray().Select(d => new Definition(
            (string?)d!["name"] ?? "", (string?)d["statement"] ?? "",
            (string?)d["consequence"] ?? "", Refs(d["evidenceFor"]))).ToImmutableArray()
            ?? ImmutableArray<Definition>.Empty,
        (string?)node["whyUndecidable"] ?? "");
}
