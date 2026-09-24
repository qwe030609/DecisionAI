// ============================================================================
//  Evidence Allocator（Rev2 §7 Phase 3）
//
//  證據不是越多越好，而是「在預算內，哪一份最能改變結論」。
//  這跟 EVOI 是同一條原則，只是作用在取得證據而不是做實驗：
//  一份無論內容如何都不會改變推薦行動的證據，蒐集它只會增加信心，不會增加正確性。
//
//  三個因子，全部是確定性的，沒有一個來自 LLM 自評：
//    · 區分力   這份證據能把目前信念分布拉開多少（期望熵下降）
//    · 可靠度   來源查表（Core.Domain.Evidence.ReliabilityOf），不是逐筆估計
//    · 成本     取得它要花多少（人力、停機、錢）
//
//  另外有一條硬規則：已經被 InjectionGuard 標記過的來源不得因為「區分力高」而被優先取得。
//  可疑來源的高區分力恰恰是攻擊者想要的效果。
// ============================================================================

using System.Collections.Immutable;
using DecisionAI.Core;
using DecisionAI.Core.Domain;

namespace DecisionAI.Modules.Evidence;

/// <summary>一份「還沒取得」的證據：它可能支持或反駁哪些假設，是由題目結構決定的事實，不是 LLM 估的。</summary>
public sealed record EvidenceCandidate(
    string Id,
    EvidenceSource Source,
    string What,
    ImmutableArray<string> BearsOnClaims,
    double Cost,
    bool Suspicious = false);

public sealed record EvidencePlan(
    ImmutableArray<(EvidenceCandidate Candidate, double Value, string Why)> Acquire,
    ImmutableArray<(EvidenceCandidate Candidate, double Value, string Why)> Skip,
    double SpentBudget)
{
    public string Render() =>
        $"取得 {Acquire.Length} 份（花費 {SpentBudget:F2}）、略過 {Skip.Length} 份" +
        (Acquire.Length > 0 ? "：" + string.Join("、", Acquire.Select(a => $"{a.Candidate.Id}({a.Value:F2})")) : "");
}

public interface IEvidenceAllocator
{
    EvidencePlan Allocate(IReadOnlyList<EvidenceCandidate> candidates,
                          IReadOnlyDictionary<string, double> beliefs,
                          double budget);
}

public sealed class EvidenceAllocator : IEvidenceAllocator
{
    /// <summary>價值低於這個門檻就不取得——「反正拿了也不會變」的證據只會讓人更有信心。</summary>
    public double MinValue { get; init; } = 0.01;

    /// <summary>可疑來源的價值折半，並且永遠排在最後——高區分力正是注入攻擊想要的效果。</summary>
    public double SuspiciousPenalty { get; init; } = 0.5;

    public EvidencePlan Allocate(IReadOnlyList<EvidenceCandidate> candidates,
                                 IReadOnlyDictionary<string, double> beliefs, double budget)
    {
        var acquire = ImmutableArray.CreateBuilder<(EvidenceCandidate, double, string)>();
        var skip = ImmutableArray.CreateBuilder<(EvidenceCandidate, double, string)>();

        var scored = candidates
            .Select(c => (c, v: Value(c, beliefs), why: Why(c, beliefs)))
            .OrderByDescending(x => x.c.Suspicious ? 0 : 1)                      // 可疑來源永遠最後
            .ThenByDescending(x => x.v / Math.Max(0.01, x.c.Cost))               // 單位成本價值
            .ThenBy(x => x.c.Id, StringComparer.Ordinal)
            .ToList();

        double spent = 0;
        foreach (var (c, v, why) in scored)
        {
            if (v < MinValue) { skip.Add((c, v, $"{why}；價值 {v:F3} 低於門檻 → 拿了也不會改變結論")); continue; }
            if (spent + c.Cost > budget) { skip.Add((c, v, $"{why}；預算只剩 {budget - spent:F2}，這份要 {c.Cost:F2}")); continue; }
            spent += c.Cost;
            acquire.Add((c, v, why));
        }

        return new EvidencePlan(acquire.ToImmutable(), skip.ToImmutable(), Math.Round(spent, 4));
    }

    /// <summary>
    /// 價值 = 可靠度 × 區分力。區分力用「這份證據涉及的假設的機率集中度」估計：
    /// 只涉及一條假設（而且那條還不確定）時最有價值；涉及全部假設等於什麼都沒分開。
    /// </summary>
    internal double Value(EvidenceCandidate c, IReadOnlyDictionary<string, double> beliefs)
    {
        if (beliefs.Count == 0) return Core.Domain.Evidence.ReliabilityOf(c.Source) * (c.Suspicious ? SuspiciousPenalty : 1);

        double mass = c.BearsOnClaims.Sum(id => beliefs.GetValueOrDefault(id, 0));
        // 分割得越接近一半一半，能拿到的資訊越多；全支持或全不支持都分不開東西
        double split = 1 - Math.Abs(0.5 - Math.Clamp(mass, 0, 1)) * 2;
        double v = Core.Domain.Evidence.ReliabilityOf(c.Source) * split;
        return Math.Round(v * (c.Suspicious ? SuspiciousPenalty : 1), 4);
    }

    private string Why(EvidenceCandidate c, IReadOnlyDictionary<string, double> beliefs)
    {
        double mass = c.BearsOnClaims.Sum(id => beliefs.GetValueOrDefault(id, 0));
        return $"{c.Source}（可靠度 {Core.Domain.Evidence.ReliabilityOf(c.Source):F2}）涉及 {mass:P0} 的機率質量" +
               (c.Suspicious ? "；來源可疑 → 價值折半並排在最後" : "");
    }
}

/// <summary>Mutation switch：不排序、不看價值，預算花完為止。</summary>
public sealed class GreedyByCostAllocator : IEvidenceAllocator
{
    public EvidencePlan Allocate(IReadOnlyList<EvidenceCandidate> candidates,
                                 IReadOnlyDictionary<string, double> beliefs, double budget)
    {
        var acquire = ImmutableArray.CreateBuilder<(EvidenceCandidate, double, string)>();
        double spent = 0;
        foreach (var c in candidates.OrderBy(c => c.Cost).ThenBy(c => c.Id, StringComparer.Ordinal))
            if (spent + c.Cost <= budget) { spent += c.Cost; acquire.Add((c, 1, "（壞的守門件）只看便宜，不看有沒有用")); }
        return new EvidencePlan(acquire.ToImmutable(),
            ImmutableArray<(EvidenceCandidate, double, string)>.Empty, Math.Round(spent, 4));
    }
}
