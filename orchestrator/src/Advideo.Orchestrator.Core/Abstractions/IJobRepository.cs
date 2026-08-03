using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>
/// Tracks job state so the API can report status and workers can update
/// progress. In-memory for local dev; a real DB (e.g. Postgres) in production.
/// </summary>
public interface IJobRepository
{
    Task UpsertAsync(VideoJob job, CancellationToken ct = default);
    Task<VideoJob?> GetAsync(string videoId, CancellationToken ct = default);
    Task<IReadOnlyList<VideoJob>> ListAsync(CancellationToken ct = default);
}
