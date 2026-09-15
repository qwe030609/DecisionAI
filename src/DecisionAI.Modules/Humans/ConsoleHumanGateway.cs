// ============================================================================
//  Humans（Phase 1 最小版）：Console 閘。PresentationPolicy 在 Phase 2。
// ============================================================================

using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Humans;

public sealed class ConsoleHumanGateway : IHumanGateway
{
    public Task<HumanVerdict> RequestAsync(HumanRequest req, CancellationToken ct = default)
    {
        if (Console.IsInputRedirected) return Task.FromResult(new HumanVerdict(true, "無人值守：自動同意"));   // 無人值守
        Console.WriteLine();
        foreach (var line in req.Context) Console.WriteLine("  " + line);          // 證據 / 最壞情況先呈現
        Console.Write($"  [{req.Role}] {req.What}？(y/N) ");
        bool ok = (Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new HumanVerdict(ok, ok ? "console 同意" : "console 拒絕"));
    }
}
