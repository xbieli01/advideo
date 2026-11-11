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

    public async Task ApplyAsync(IPlatformRepository platforms, ICampaignRepository campaigns, CancellationToken ct = default)
    {
        foreach (var p in Platforms) await platforms.UpsertAsync(p, ct);
        foreach (var c in Campaigns) await campaigns.UpsertAsync(c, ct);
    }
}
