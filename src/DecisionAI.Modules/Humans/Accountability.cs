// ============================================================================
//  五角色的最後一個：Accountable（Rev2 §4.13 / Phase 3）
//
//  前四個角色都在流程「裡面」：核准、評分、仲裁、給效用。第五個在流程「外面」——
//  他要能在事後回答「這個決定是怎麼來的、誰簽了什麼、當時系統宣稱了什麼」。
//
//  這份帳本刻意從事件流投影，而不是另外記一份：另外記的那份一定會跟事件流不同步，
//  而在需要它的時候（出事之後）沒有人能分辨哪一份是真的。
//
//  它也刻意記錄「系統當時宣稱了什麼」而不只是「系統做了什麼」：
//  事後看一個決定對不對，不能只看結果，要看當時給的保證是否誠實。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Journal;

namespace DecisionAI.Modules.Humans;

public sealed record SignOff(string Role, string Actor, string What, bool Approved, string Rationale,
                             double? Seconds, bool SawRecommendationFirst);

public sealed record AccountabilityRecord(
    string CaseId,
    string Problem,
    string? RecommendedAction,
    string AssuranceGrade,
    ImmutableArray<string> ClaimedGuarantees,
    ImmutableArray<SignOff> SignOffs,
    ImmutableArray<string> Degradations,
    string? Outcome)
{
    public bool HasHumanSignOff => SignOffs.Any(s => s.Approved);

    public string Render()
    {
        var lines = new List<string> { $"[{CaseId}] {Problem}", $"  建議：{RecommendedAction ?? "（無，或已拒答）"}", $"  保證：{AssuranceGrade}" };
        foreach (var g in ClaimedGuarantees) lines.Add($"    · {g}");
        foreach (var d in Degradations) lines.Add($"    ! 降級：{d}");
        lines.Add(SignOffs.Length == 0 ? "  簽核：無人簽核"
            : "  簽核：" + string.Join("；", SignOffs.Select(s =>
                $"{s.Role}/{s.Actor} {(s.Approved ? "同意" : "拒絕")}" +
                (s.Seconds is { } sec ? $"（{sec:F1} 秒{(s.SawRecommendationFirst ? "、先看到建議" : "")}）" : ""))));
        if (Outcome is not null) lines.Add($"  真實結果：{Outcome}");
        return string.Join("\n", lines);
    }
}

public interface IAccountabilityLedger
{
    AccountabilityRecord Build(ICaseJournal journal);
}

public sealed class AccountabilityLedger : IAccountabilityLedger
{
    public AccountabilityRecord Build(ICaseJournal journal)
    {
        var s = journal.State;
        var report = s.Assurance;
        var guarantees = ImmutableArray.CreateBuilder<string>();
        if (report?.Capability is { } cap)
            guarantees.Add($"能力層：通過 {cap.BestPassedLevel}，上限 {cap.Cap:F2}");
        if (report?.Coverage is { } cov)
            guarantees.Add($"覆蓋層：{cov.TargetCoverage:P0}，預測集合 {{{string.Join(", ", cov.PredictionSet)}}}");
        if (report?.Stability is { } st)
        {
            guarantees.Add($"穩定層：機率擾動 {st.Robustness:P0}" +
                (st.DecisionStabilityUnderResampling is { } q ? $"、假設集重抽樣 {q:P0}" : "") +
                (st.PosteriorOrderStableUnderLikertShift is { } k ? $"、似然 ±1 級{(k ? "排序不變" : "排序會變")}" : ""));
        }
        if (report?.LikelihoodSensitive == true)
            guarantees.Add("已標示：結論對似然估計敏感");
        if (report?.Abstention is { Abstained: true } ab)
            guarantees.Add($"拒答：{ab.Code} — {ab.Explanation}（替代交付 {ab.Payload?.Kind ?? "無"}）");

        var signoffs = journal.Events.OfType<HumanActed>()
            .Select(h => new SignOff(h.HumanRole, h.ActorId, h.What, h.Approved, h.Rationale, h.Seconds, h.SawRecommendationFirst))
            .ToImmutableArray();

        return new AccountabilityRecord(
            s.Id,
            s.Request?.Problem ?? "",
            s.Decision?.RecommendedAction,
            report?.ConservativeGrade() ?? "（尚未發出 Assurance）",
            guarantees.ToImmutable(),
            signoffs,
            report?.Capability?.DegradedReasons ?? ImmutableArray<string>.Empty,
            s.Outcome?.Note);
    }
}
