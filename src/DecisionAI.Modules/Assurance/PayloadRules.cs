// ============================================================================
//  替代交付的規則與驗證（Rev2 §4.10）——確定性區，不得出現 ILlm。
//  兩條容易漏掉的規則都在這裡：
//   1) payload 是 LLM 產生的 → 事實部分必須引用存在的 Evidence ID，領先指標必須可觀察。
//   2) 情境對稱性 → 樂觀寫 500 字引 5 條、悲觀寫 50 字引 1 條，讀者一定讀得出方向，
//      即使系統從未說過方向。不對稱就退回重寫。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core.Assurance;
using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Assurance;

public sealed record PayloadVerdict(bool Ok, ImmutableArray<VerificationResult> Results)
{
    public IEnumerable<string> Failures => Results.Where(r => r.Pass == false).Select(r => r.Note);
}

public interface IPayloadVerifier
{
    PayloadVerdict Verify(SubstitutePayload payload, ReasonCode code, IEvidenceStore store);
}

public sealed class PayloadVerifier : IPayloadVerifier
{
    public const string Id = "payload-rule";

    /// <summary>各情境的證據數與篇幅容忍範圍。超過就是變相的方向暗示。</summary>
    public double MaxEvidenceRatio { get; init; } = 2.0;
    public double MaxLengthRatio { get; init; } = 2.5;

    public PayloadVerdict Verify(SubstitutePayload payload, ReasonCode code, IEvidenceStore store)
    {
        var r = ImmutableArray.CreateBuilder<VerificationResult>();
        void Check(bool ok, string note, string? claim = null)
            => r.Add(new VerificationResult(Id, VerifierLevel.L2_Rule, claim, ok, ok ? 1 : 0, note));

        // 0) 型別綁定：payload 必須宣告自己適用於這個 ReasonCode
        Check(payload.ValidFor.Contains(code),
            payload.ValidFor.Contains(code)
                ? $"型別綁定相符：{payload.Kind} 適用於 {code}"
                : $"型別錯配：{payload.Kind} 不得掛在 {code} 上（允許：{string.Join("/", payload.ValidFor)}）");

        // 1) 事實必須引用存在的證據
        var missing = payload.EvidenceRefs.Where(id => !store.Exists(id)).ToList();
        Check(missing.Count == 0, missing.Count == 0
            ? $"引用 {payload.EvidenceRefs.Length} 條證據，全部存在"
            : $"引用不存在的證據：{string.Join(",", missing)}");

        switch (payload)
        {
            case ScenarioBriefing sb:
                VerifyBriefing(sb, Check);
                break;

            case ConsiderationMap cm:
                Check(cm.Items.Length >= 2, $"考量項 {cm.Items.Length} 條（至少 2 條才稱得上地圖）");
                Check(cm.ForHumans.Length >= 1, $"留給人的開放問題 {cm.ForHumans.Length} 條");
                Check(cm.Items.All(i => i.WhoItMattersTo.Trim().Length > 0), "每條考量都標明了對誰重要");
                break;

            case DefinitionMap dm:
                Check(dm.Definitions.Length >= 2, $"定義 {dm.Definitions.Length} 種（少於 2 種就不是定義爭議）");
                Check(dm.Definitions.All(d => d.Consequence.Trim().Length > 0), "每個定義都寫出了各自的推論後果");
                Check(dm.WhyUndecidable.Trim().Length > 0, "說明了為什麼系統不裁決");
                break;

            case DelegationHandoff dh:
                Check(dh.ToolId.Trim().Length > 0 && dh.HowToRead.Trim().Length > 0, "轉介對象與判讀方式齊全");
                Check(dh.InputsRequired.Length > 0, $"列出了 {dh.InputsRequired.Length} 項需要準備的輸入");
                break;

            case BlockedHandback bh:
                Check(bh.Needed.Length > 0, $"列出 {bh.Needed.Length} 項缺什麼");
                Check(bh.HowToResubmit.Trim().Length > 0, "說明了補齊後如何重新提交");
                break;
        }

        var all = r.ToImmutable();
        return new PayloadVerdict(all.All(x => x.Pass == true), all);
    }

    private void VerifyBriefing(ScenarioBriefing sb, Action<bool, string, string?> check)
    {
        check(sb.Scenarios.Length >= 2, $"情境 {sb.Scenarios.Length} 個（少於 2 個就是變相的方向預測）", null);

        var badIndicators = sb.Scenarios.SelectMany(s => s.Indicators).Where(i => !i.IsObservable).ToList();
        check(badIndicators.Count == 0, badIndicators.Count == 0
            ? "所有領先指標都有資料來源、閾值與觀察頻率"
            : $"領先指標不可觀察（缺來源/閾值/頻率）：{string.Join("、", badIndicators.Select(i => i.Name))}", null);

        if (sb.Scenarios.Length >= 2)
        {
            // 情境對稱性：證據數與篇幅都不能差太多
            var evCounts = sb.Scenarios.Select(s => Math.Max(1, s.Basis.Sum(b => b.EvidenceFor.Length))).ToList();
            var lengths  = sb.Scenarios.Select(s => Math.Max(1, s.Basis.Sum(b => b.Text.Length))).ToList();
            double evRatio = evCounts.Max() / (double)evCounts.Min();
            double lenRatio = lengths.Max() / (double)lengths.Min();

            check(evRatio <= MaxEvidenceRatio,
                $"情境證據數 {string.Join(":", evCounts)}，比值 {evRatio:F2}" +
                (evRatio <= MaxEvidenceRatio ? "（對稱）" : $" > {MaxEvidenceRatio:F1} → 變相的方向暗示，退回重寫"), null);
            check(lenRatio <= MaxLengthRatio,
                $"情境篇幅 {string.Join(":", lengths)} 字，比值 {lenRatio:F2}" +
                (lenRatio <= MaxLengthRatio ? "（對稱）" : $" > {MaxLengthRatio:F1} → 變相的方向暗示，退回重寫"), null);
        }
    }
}

/// <summary>Mutation switch：停用情境對稱性與型別綁定檢查。</summary>
public sealed class PermissivePayloadVerifier : IPayloadVerifier
{
    public PayloadVerdict Verify(SubstitutePayload payload, ReasonCode code, IEvidenceStore store)
        => new(true, ImmutableArray.Create(
            new VerificationResult("payload-rule", VerifierLevel.L2_Rule, null, true, 1, "（壞的守門件）一律通過")));
}

/// <summary>不需要 LLM 就能產生的替代交付。</summary>
public static class DeterministicPayloads
{
    public static BlockedHandback NoVerifier(VerifierLevel best) => new(
        $"沒有 L3 以上的 verifier（目前最高 {best}）",
        ImmutableArray.Create(
            new MissingCapability("可執行測試 / 模擬器（L3）", "能在受控條件下重現並檢核假設，是這套系統唯一的價值來源"),
            new MissingCapability("實驗環境：HIL / staging / A-B（L4）", "能在真實系統上區分競爭假設")),
        "接上其中一種驗證器後重新提交；在那之前系統只能整理證據，不能給帶信心的結論。");

    public static BlockedHandback Halted(string reason, ImmutableArray<string> skipped) => new(
        reason,
        skipped.Select(s => new MissingCapability($"未執行的步驟 {s}", "被上游中止擋下，沒有結果可背書")).ToImmutableArray(),
        "修正被攔下的項目後可從頭重跑；已完成步驟的紀錄都保留在事件流裡。");

    public static BlockedHandback NoAdmissibleAction(string reason) => new(
        reason,
        ImmutableArray.Create(
            new MissingCapability("放寬可接受損失，或增加行動選項", "目前每個選項的最壞情況都超過政策上限")),
        "由效用矩陣的擁有者調整風險政策或補上新選項後重新提交。");
}
