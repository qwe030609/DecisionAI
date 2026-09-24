// ============================================================================
//  PresentationPolicy（Rev2 §4.13 / Phase 2）：反 automation bias。
//
//  這條防護刻意放在伺服端而不是 UI：若交給前端，任何新 UI（手機、Slack、
//  之後某個人寫的小工具）都可能繞過。「先給答案再問人同不同意」會直接誘發
//  確認偏誤與橡皮圖章化——設計上必須是「做不到」，而不是「約定不要做」。
//
//  五個角色的呈現需求不同：
//   · Approver  證據與最壞情況先呈現，系統建議最後才揭示
//   · Rater     完全盲評：不得看到模型身分，也不得看到 LLM 自評信心
//   · Arbiter   只看兩邊差異，不看誰提的
//   · UtilityOwner 只填效用，不該看到系統偏好哪個行動
//   · Accountable  要看全部，包含降級與拒答理由
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Humans;

public interface IPresentationPolicy
{
    HumanRequest Shape(HumanRequest raw, HumanRole role);
}

public sealed class PresentationPolicy : IPresentationPolicy
{
    /// <summary>候選長度正規化上限：讓「寫得長」不自動等於「寫得好」。</summary>
    public int NormalizedLength { get; init; } = 320;

    public HumanRequest Shape(HumanRequest raw, HumanRole role) => role switch
    {
        HumanRole.Approver     => ForApprover(raw),
        HumanRole.Rater        => Blind(raw),
        HumanRole.Arbiter      => Blind(raw with { SystemRecommendation = null }),
        HumanRole.UtilityOwner => raw with { SystemRecommendation = null, RecommendationRevealed = false,
                                             Context = Scrub(raw.Context) },
        HumanRole.Accountable  => raw with { RecommendationRevealed = true },
        _                      => Blind(raw)
    };

    /// <summary>
    /// 證據與最壞情況先、系統建議後。RecommendationRevealed = false 代表建議還沒揭示；
    /// 呼叫端要等人先表態才能呼叫 Reveal。
    /// </summary>
    private static HumanRequest ForApprover(HumanRequest raw)
    {
        var ordered = raw.Context
            .OrderBy(line => Rank(line))
            .ThenBy(line => raw.Context.IndexOf(line))
            .ToImmutableArray();
        return raw with { Context = ordered, RecommendationRevealed = false };
    }

    /// <summary>0 = 證據 / 最壞情況，1 = 其他，2 = 任何像系統建議的行。</summary>
    private static int Rank(string line)
    {
        if (LooksLikeRecommendation(line)) return 2;
        if (line.Contains("證據") || line.Contains("最壞") || line.Contains("worst")
            || line.Contains("風險") || line.Contains("evidence")) return 0;
        return 1;
    }

    private static bool LooksLikeRecommendation(string line)
        => line.Contains("建議") || line.Contains("推薦") || line.Contains("recommend");

    /// <summary>盲評：模型身分與 LLM 自評信心一律拔掉，候選順序不在這裡洗（由 ballot 洗過）。</summary>
    private HumanRequest Blind(HumanRequest raw) => raw with
    {
        Context = Scrub(raw.Context),
        Options = raw.Options.Select(o => Normalize(Strip(o), NormalizedLength)).ToImmutableArray(),
        RecommendationRevealed = false
    };

    /// <summary>把系統建議相關的行從 context 拔掉——UtilityOwner 與盲評者都不該看到。</summary>
    private static ImmutableArray<string> Scrub(ImmutableArray<string> context)
        => context.Where(l => !LooksLikeRecommendation(l)).Select(Strip).ToImmutableArray();

    /// <summary>拔掉模型身分標記與自評信心：[gpt-4o｜自評信心 90%] 這種前綴。</summary>
    internal static string Strip(string text)
    {
        var t = text;
        // 方括號前綴：只在確實含有身分 / 自評字樣時才拔，避免誤刪內容
        if (t.StartsWith('[') )
        {
            int close = t.IndexOf(']');
            if (close > 0)
            {
                var head = t[1..close];
                if (head.Contains("自評") || head.Contains("信心") || head.Contains("confidence")
                    || Identities.Any(id => head.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    t = t[(close + 1)..].TrimStart();
            }
        }
        foreach (var id in Identities)
            t = t.Replace(id, "（模型身分已隱藏）", StringComparison.OrdinalIgnoreCase);
        return t;
    }

    /// <summary>會被遮蔽的身分字樣。這是保守清單，真正的保證來自「候選文字由伺服端組」。</summary>
    internal static readonly ImmutableArray<string> Identities = ImmutableArray.Create(
        "gpt-4o", "gpt-4", "gpt", "claude", "gemini", "llama", "mistral", "grok", "qwen", "deepseek");

    internal static string Normalize(string text, int max)
    {
        var t = string.Join(" ", text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= max ? t : t[..max] + "…";
    }
}

/// <summary>
/// 建議的揭示是一個獨立動作：人先表態、才呼叫這個。把它做成分開的函數，
/// 是為了讓「先看到建議」在型別上就不方便發生。
/// </summary>
public static class RecommendationReveal
{
    public static HumanRequest Reveal(HumanRequest shaped, string recommendation)
        => shaped with
        {
            SystemRecommendation = recommendation,
            RecommendationRevealed = true,
            Context = shaped.Context.Add($"系統建議（於人表態後揭示）：{recommendation}")
        };
}

/// <summary>Mutation switch：先給答案再問人同不同意——橡皮圖章化的標準做法。</summary>
public sealed class RecommendationFirstPolicy : IPresentationPolicy
{
    public HumanRequest Shape(HumanRequest raw, HumanRole role) => raw with
    {
        RecommendationRevealed = true,
        Context = raw.SystemRecommendation is { } r
            ? ImmutableArray.Create($"系統建議：{r}（（壞的守門件）建議先於證據呈現）").AddRange(raw.Context)
            : raw.Context
    };
}
