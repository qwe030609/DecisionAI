// ============================================================================
//  Evidence Store（記憶體版）。正式版換 Postgres + pgvector，介面不變。
// ============================================================================

using DecisionAI.Core.Domain;
using DecisionAI.Core.Ports;

namespace DecisionAI.Modules.Evidence;

public sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly Dictionary<string, Core.Domain.Evidence> _byId = new();
    private readonly IClock _clock;
    private readonly object _lock = new();

    public InMemoryEvidenceStore(IClock clock) => _clock = clock;

    public Core.Domain.Evidence Admit(EvidenceDraft d)
    {
        lock (_lock)
        {
            var ev = new Core.Domain.Evidence($"EV-{_byId.Count + 1:000}", d.Source, d.Origin, d.Content, _clock.UtcNow);
            _byId[ev.Id] = ev;
            return ev;
        }
    }

    public bool Exists(string id) { lock (_lock) return _byId.ContainsKey(id); }
    public Core.Domain.Evidence? Get(string id) { lock (_lock) return _byId.GetValueOrDefault(id); }
}
