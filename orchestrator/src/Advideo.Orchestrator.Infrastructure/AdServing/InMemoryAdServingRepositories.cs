using System.Collections.Concurrent;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.AdServing;

/// <summary>In-memory platform store (dev / M2). Swap for Postgres behind the same interface.</summary>
public sealed class InMemoryPlatformRepository : IPlatformRepository
{
    private readonly ConcurrentDictionary<string, PlatformConfig> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<PlatformConfig?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);

    public Task<IReadOnlyList<PlatformConfig>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlatformConfig>>(_items.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList());

    public Task UpsertAsync(PlatformConfig platform, CancellationToken ct = default)
    {
        _items[platform.Id] = platform;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(_items.TryRemove(id, out _));
}

/// <summary>In-memory campaign store (dev / M2). Swap for Postgres behind the same interface.</summary>
public sealed class InMemoryCampaignRepository : ICampaignRepository
{
    private readonly ConcurrentDictionary<string, Campaign> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var c) ? c : null);

    public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Campaign>>(_items.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<Campaign>> ListServableAsync(string platformId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Campaign>>(_items.Values
            .Where(c => c.Status == CampaignStatus.Active)
            .Where(c => c.Targeting.Platforms.Count == 0 || c.Targeting.Platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase))
            .ToList());

    public Task UpsertAsync(Campaign campaign, CancellationToken ct = default)
    {
        _items[campaign.Id] = campaign;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(_items.TryRemove(id, out _));
}
