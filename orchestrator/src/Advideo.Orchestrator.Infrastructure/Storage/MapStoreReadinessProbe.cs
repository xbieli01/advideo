using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.Storage;

/// <summary>Map store reachable (a lookup of a non-existing key must not throw).</summary>
public sealed class MapStoreReadinessProbe(IMapStore store) : IReadinessProbe
{
    public string Name => "mapStore";
    public Task CheckAsync(CancellationToken ct) => store.ExistsAsync(store.KeyOf("__readyz__"), ct);
}
