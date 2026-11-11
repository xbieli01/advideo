using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Npgsql;

namespace Advideo.Orchestrator.Infrastructure.Postgres;

public sealed class PostgresPlatformRepository(NpgsqlDataSource db) : IPlatformRepository
{
    private readonly PostgresDocumentStore<PlatformConfig> _docs = new(db, "advideo_platforms");

    public Task<PlatformConfig?> GetAsync(string id, CancellationToken ct = default) => _docs.GetAsync(id.ToLowerInvariant(), ct);
    public Task<IReadOnlyList<PlatformConfig>> ListAsync(CancellationToken ct = default) => _docs.ListAsync(null, ct);
    public Task UpsertAsync(PlatformConfig p, CancellationToken ct = default) => _docs.UpsertAsync(p.Id.ToLowerInvariant(), p, ct);
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => _docs.DeleteAsync(id.ToLowerInvariant(), ct);
}

public sealed class PostgresCampaignRepository(NpgsqlDataSource db) : ICampaignRepository
{
    private readonly PostgresDocumentStore<Campaign> _docs = new(db, "advideo_campaigns");

    public Task<Campaign?> GetAsync(string id, CancellationToken ct = default) => _docs.GetAsync(id.ToLowerInvariant(), ct);
    public Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default) => _docs.ListAsync(null, ct);

    public async Task<IReadOnlyList<Campaign>> ListServableAsync(string platformId, CancellationToken ct = default)
    {
        // status filtered in SQL (indexed); platform list is tiny, filtered in memory (case-insensitive)
        var active = await _docs.ListAsync("doc->>'status' = 'active'", ct);
        return active
            .Where(c => c.Targeting.Platforms.Count == 0 || c.Targeting.Platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public Task UpsertAsync(Campaign c, CancellationToken ct = default) => _docs.UpsertAsync(c.Id.ToLowerInvariant(), c, ct);
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => _docs.DeleteAsync(id.ToLowerInvariant(), ct);
}

public sealed class PostgresJobRepository(NpgsqlDataSource db) : IJobRepository
{
    private readonly PostgresDocumentStore<VideoJob> _docs = new(db, "advideo_jobs");

    public Task UpsertAsync(VideoJob job, CancellationToken ct = default) =>
        _docs.UpsertAsync(job.VideoId, job with { UpdatedAt = DateTimeOffset.UtcNow }, ct);

    public Task<VideoJob?> GetAsync(string videoId, CancellationToken ct = default) => _docs.GetAsync(videoId, ct);
    public Task<IReadOnlyList<VideoJob>> ListAsync(CancellationToken ct = default) => _docs.ListAsync(null, ct);
}
