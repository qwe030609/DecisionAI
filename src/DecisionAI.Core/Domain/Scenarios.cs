// ============================================================================
//  效用矩陣的「情境鍵」對齊。
//
//  效用矩陣是人（UtilityOwner）在案子開始前就給的，所以它不可能用本案才產生的
//  本地編號（H1/H2）當鍵——本地編號取決於哪個 solver 先被指派，每次可能不同。
//  人自然會用「如果根因是競態」這種說法，也就是 mechanism 或 catalog 條目。
//
//  這裡把三種寫法都接受，並一律對齊到本地編號：
//    本地編號 H1 > catalog id（跨案穩定）> mechanism（人最常用的說法）
//  對不到的鍵原樣保留——保留比猜好，決策引擎會看到 0 效用而不是錯誤效用。
// ============================================================================

using System.Collections.Immutable;

namespace DecisionAI.Core.Domain;

public static class Scenarios
{
    /// <summary>一個主張的所有可接受寫法，依優先序。</summary>
    public static IEnumerable<string> AliasesOf(string localId, string catalogId, string mechanism)
    {
        yield return localId;
        if (catalogId.Length > 0) yield return catalogId;
        if (mechanism.Length > 0) yield return mechanism;
    }

    /// <summary>把效用矩陣的情境鍵改寫成本案的本地編號。</summary>
    public static ImmutableArray<ActionOption> Align(
        ImmutableArray<ActionOption> actions,
        IEnumerable<(string LocalId, string CatalogId, string Mechanism)> claims)
    {
        if (actions.IsDefaultOrEmpty) return actions;
        var list = claims.ToList();

        return actions.Select(a =>
        {
            var b = ImmutableDictionary.CreateBuilder<string, double>();
            foreach (var kv in a.UtilityByScenario) b[kv.Key] = kv.Value;

            foreach (var c in list)
            {
                if (b.ContainsKey(c.LocalId)) continue;                       // 已經用本地編號寫的，不動
                foreach (var alias in AliasesOf(c.LocalId, c.CatalogId, c.Mechanism))
                    if (b.TryGetValue(alias, out var u)) { b[c.LocalId] = u; break; }
            }
            return a with { UtilityByScenario = b.ToImmutable() };
        }).ToImmutableArray();
    }
}
