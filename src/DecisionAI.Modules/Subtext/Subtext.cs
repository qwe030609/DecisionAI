// ============================================================================
//  Subtext（Rev2 §4.12 / Phase 2）：沒有 verifier 的題型，人是唯一的量尺。
//
//  三條規則，每一條都在防一種會讓「人評」失去意義的失效：
//   1) 只把「分歧最大的 top-k」送人評 —— 人評額度很貴，花在大家都同意的樣本上是浪費。
//   2) 盲評：隱藏模型身分、隨機化順序、長度正規化 —— 對應 MR-2（position bias）
//      與 MR-6（verbosity bias）。看得到是哪家模型寫的，評分就不再是評內容。
//   3) 主動學習挑的樣本只能進「校準集」；「稽核集」永遠隨機抽 ——
//      主動學習破壞可交換性，混進稽核集會讓覆蓋率量不準（與 Conformal 同一條紀律）。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;
using DecisionAI.Modules.Humans;

namespace DecisionAI.Modules.Subtext;

/// <summary>一個 persona：潛台詞題型裡的「立場」，不是 agent。</summary>
public sealed record Persona(string Id, string Label, string Stance)
{
    /// <summary>用來偵測「複製 persona」——MR-5 要求複製不得讓多樣性上升。</summary>
    public string Fingerprint => ClaimFrame.Norm(Stance);
}

public interface IPersonaBank
{
    ImmutableArray<Persona> All { get; }
    /// <summary>去重後的 persona：指紋相同的只留第一個（MR-5 的執行點）。</summary>
    ImmutableArray<Persona> Distinct();
}

public sealed class PersonaBank : IPersonaBank
{
    public PersonaBank(params Persona[] personas) => All = personas.ToImmutableArray();
    public ImmutableArray<Persona> All { get; }

    public ImmutableArray<Persona> Distinct()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return All.Where(p => seen.Add(p.Fingerprint)).ToImmutableArray();
    }
}

// ── 分歧評分 ────────────────────────────────────────────────────────────

/// <summary>一個待評樣本：同一個問題下不同 agent / persona 給的讀法。</summary>
public sealed record SubtextSample(string Id, string Question, ImmutableArray<Reading> Readings);

/// <summary>一種讀法。AgentId 只在伺服端留存，盲評時不會進 HumanRequest。</summary>
public sealed record Reading(string AgentId, string Text, double SelfReportedConfidence);

public interface IDisagreementScorer
{
    /// <summary>0 = 所有讀法一致，1 = 完全分歧。</summary>
    double Score(SubtextSample sample);
}

/// <summary>以 n-gram Jaccard 的平均「不相似度」當分歧度；不需要 LLM，故可放確定性區。</summary>
public sealed class NGramDisagreement : IDisagreementScorer
{
    public int N { get; init; } = 3;

    public double Score(SubtextSample s)
    {
        var sets = s.Readings.Select(r => Grams(r.Text)).Where(g => g.Count > 0).ToList();
        if (sets.Count < 2) return 0;
        double total = 0; int pairs = 0;
        for (int i = 0; i < sets.Count; i++)
            for (int j = i + 1; j < sets.Count; j++)
            {
                double inter = sets[i].Intersect(sets[j]).Count();
                double union = sets[i].Union(sets[j]).Count();
                total += union <= 0 ? 0 : 1 - inter / union;
                pairs++;
            }
        return pairs == 0 ? 0 : Math.Clamp(total / pairs, 0, 1);
    }

    private HashSet<string> Grams(string text)
    {
        var t = ClaimFrame.Norm(text).Replace(" ", "");
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + N <= t.Length; i++) set.Add(t.Substring(i, N));
        return set;
    }
}

// ── 主動抽樣 ────────────────────────────────────────────────────────────

public enum SamplePurpose { Calibration, Audit }

public sealed record SampleSelection(SubtextSample Sample, SamplePurpose Purpose, double Disagreement, string Why);

public interface IActiveSampler
{
    /// <summary>
    /// 回傳要送人評的樣本。校準集用主動學習（挑分歧最大的），稽核集必須隨機抽——
    /// 兩份的用途不同，混用會讓覆蓋率量測失效。
    /// </summary>
    ImmutableArray<SampleSelection> Select(IReadOnlyList<SubtextSample> pool, int calibrationK, int auditK, IRandomSource rng);
}

public sealed class TopKDisagreementSampler : IActiveSampler
{
    private readonly IDisagreementScorer _scorer;
    public TopKDisagreementSampler(IDisagreementScorer scorer) => _scorer = scorer;

    public ImmutableArray<SampleSelection> Select(IReadOnlyList<SubtextSample> pool, int calibrationK, int auditK, IRandomSource rng)
    {
        if (pool.Count == 0) return ImmutableArray<SampleSelection>.Empty;
        var scored = pool.Select(s => (s, d: _scorer.Score(s)))
                         .OrderByDescending(x => x.d).ThenBy(x => x.s.Id, StringComparer.Ordinal).ToList();

        var picked = ImmutableArray.CreateBuilder<SampleSelection>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (s, d) in scored.Take(Math.Max(0, calibrationK)))
        {
            picked.Add(new SampleSelection(s, SamplePurpose.Calibration, Math.Round(d, 3),
                $"分歧度 {d:F2} 進前 {calibrationK} → 主動學習挑進校準集"));
            taken.Add(s.Id);
        }

        // 稽核集：從「全部樣本」隨機抽，不看分歧度，也不避開已被挑走的——
        // 只要抽樣是隨機的，可交換性就還在。
        var stream = rng.Fork("subtext_audit");
        var remaining = pool.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (int i = 0; i < auditK && remaining.Count > 0; i++)
        {
            var s = remaining[stream.Next(remaining.Count)];
            remaining.Remove(s);
            picked.Add(new SampleSelection(s, SamplePurpose.Audit, Math.Round(_scorer.Score(s), 3),
                "稽核集：隨機抽樣（不得用主動學習，否則覆蓋率量不準）"));
        }

        return picked.ToImmutable();
    }
}

/// <summary>Mutation switch：稽核集也用主動學習挑 → 可交換性被破壞，覆蓋率變成假的。</summary>
public sealed class ActiveAuditSampler : IActiveSampler
{
    private readonly IDisagreementScorer _scorer;
    public ActiveAuditSampler(IDisagreementScorer scorer) => _scorer = scorer;

    public ImmutableArray<SampleSelection> Select(IReadOnlyList<SubtextSample> pool, int calibrationK, int auditK, IRandomSource rng)
        => pool.Select(s => (s, d: _scorer.Score(s)))
               .OrderByDescending(x => x.d).ThenBy(x => x.s.Id, StringComparer.Ordinal)
               .Take(Math.Max(0, calibrationK) + Math.Max(0, auditK))
               .Select((x, i) => new SampleSelection(x.s, i < calibrationK ? SamplePurpose.Calibration : SamplePurpose.Audit,
                                                     Math.Round(x.d, 3), "（壞的守門件）稽核集也用主動學習挑"))
               .ToImmutableArray();
}

// ── 盲評閘 ──────────────────────────────────────────────────────────────

public sealed record BlindBallot(
    string SampleId,
    ImmutableArray<string> Options,
    ImmutableArray<string> AgentIdByPosition,   // 伺服端解盲用；不進 HumanRequest
    string Detail);

public interface IRaterGateway
{
    /// <summary>盲評：隱藏模型身分、隨機化順序、長度正規化後才送人。</summary>
    Task<RatingResult> RateAsync(SubtextSample sample, IRandomSource rng, CancellationToken ct = default);
}

public sealed record RatingResult(string SampleId, string? WinnerAgentId, int? ChosenPosition, string Rationale)
{
    public bool NoVerdict => WinnerAgentId is null;
}

public sealed class BlindRaterGateway : IRaterGateway
{
    private readonly IHumanGateway _humans;
    private readonly IPresentationPolicy _presentation;
    public BlindRaterGateway(IHumanGateway humans, IPresentationPolicy presentation)
    { _humans = humans; _presentation = presentation; }

    /// <summary>長度正規化的上限：超出就截斷，讓「寫得長」不再自動等於「寫得好」（MR-6）。</summary>
    public int NormalizedLength { get; init; } = 320;

    public async Task<RatingResult> RateAsync(SubtextSample sample, IRandomSource rng, CancellationToken ct = default)
    {
        var ballot = Prepare(sample, rng, NormalizedLength);
        var raw = new HumanRequest(sample.Id, "subtext_rate", HumanRole.Rater,
                                  $"哪一個讀法最貼近實際潛台詞？（{sample.Question}）",
                                  ImmutableArray.Create(ballot.Detail))
        {
            Options = ballot.Options
        };

        var shaped = _presentation.Shape(raw, HumanRole.Rater);
        var verdict = await _humans.RequestAsync(shaped, ct);

        int? pos = verdict.ChosenOption;
        string? winner = pos is >= 0 && pos < ballot.AgentIdByPosition.Length
            ? ballot.AgentIdByPosition[pos.Value] : null;
        return new RatingResult(sample.Id, winner, pos, verdict.Rationale);
    }

    /// <summary>純函數，好測：隨機化順序 + 隱藏身分 + 長度正規化。</summary>
    public static BlindBallot Prepare(SubtextSample sample, IRandomSource rng, int normalizedLength = 320)
    {
        var stream = rng.Fork("blind_ballot");
        var order = sample.Readings.OrderBy(r => r.AgentId, StringComparer.Ordinal).ToList();

        // Fisher–Yates：順序隨機化，否則第一個候選天生占優（MR-2）
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = stream.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var options = order.Select(r => Normalize(r.Text, normalizedLength)).ToImmutableArray();
        return new BlindBallot(sample.Id, options,
            order.Select(r => r.AgentId).ToImmutableArray(),
            $"盲評：{order.Count} 個候選已隱藏模型身分、隨機化順序、長度正規化至 {normalizedLength} 字以內");
    }

    internal static string Normalize(string text, int max)
    {
        var t = text.Trim();
        // 壓掉多餘空白，再截斷：兩者都是為了讓長度不成為評分訊號
        t = string.Join(" ", t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= max ? t : t[..max] + "…";
    }
}

/// <summary>Mutation switch：不盲評——保留模型身分、不洗順序、不正規化長度。</summary>
public sealed class IdentifiedRaterGateway : IRaterGateway
{
    private readonly IHumanGateway _humans;
    public IdentifiedRaterGateway(IHumanGateway humans) => _humans = humans;

    public async Task<RatingResult> RateAsync(SubtextSample sample, IRandomSource rng, CancellationToken ct = default)
    {
        var order = sample.Readings.OrderBy(r => r.AgentId, StringComparer.Ordinal).ToList();
        var req = new HumanRequest(sample.Id, "subtext_rate", HumanRole.Rater, sample.Question,
                                   ImmutableArray.Create("（壞的守門件）未盲評"))
        {
            Options = order.Select(r => $"[{r.AgentId}｜自評信心 {r.SelfReportedConfidence:P0}] {r.Text}").ToImmutableArray()
        };
        var v = await _humans.RequestAsync(req, ct);
        int? pos = v.ChosenOption;
        return new RatingResult(sample.Id,
            pos is >= 0 && pos < order.Count ? order[pos.Value].AgentId : null, pos, v.Rationale);
    }
}
