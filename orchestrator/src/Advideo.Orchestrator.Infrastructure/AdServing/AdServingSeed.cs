using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.AdServing;

/// <summary>Seed file with platforms and campaigns (dev/demo until a persistent store exists).</summary>
public sealed record AdServingSeed(IReadOnlyList<PlatformConfig> Platforms, IReadOnlyList<Campaign> Campaigns)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<AdServingSeed> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AdServingSeed>(fs, Json, ct)
               ?? new AdServingSeed([], []);
    }

    /// <summary>Inserts only missing items, so a persistent store keeps changes made through the API.</summary>
    public async Task<(int Platforms, int Campaigns)> ApplyAsync(IPlatformRepository platforms, ICampaignRepository campaigns, CancellationToken ct = default)
    {
        int np = 0, nc = 0;
        foreach (var p in Platforms)
            if (await platforms.GetAsync(p.Id, ct) is null) { await platforms.UpsertAsync(p, ct); np++; }
        foreach (var c in Campaigns)
            if (await campaigns.GetAsync(c.Id, ct) is null) { await campaigns.UpsertAsync(c, ct); nc++; }
        return (np, nc);
    }
}
