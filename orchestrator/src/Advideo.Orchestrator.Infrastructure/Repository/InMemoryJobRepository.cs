using System.Collections.Concurrent;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Infrastructure.Repository;

/// <summary>Thread-safe in-memory job store. Replace with a DB in production.</summary>
public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<string, VideoJob> _jobs = new();

    public Task UpsertAsync(VideoJob job, CancellationToken ct = default)
    {
        _jobs[job.VideoId] = job with { UpdatedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task<VideoJob?> GetAsync(string videoId, CancellationToken ct = default)
        => Task.FromResult(_jobs.TryGetValue(videoId, out var job) ? job : null);

    public Task<IReadOnlyList<VideoJob>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VideoJob>>(_jobs.Values
            .OrderByDescending(j => j.CreatedAt).ToList());
}
