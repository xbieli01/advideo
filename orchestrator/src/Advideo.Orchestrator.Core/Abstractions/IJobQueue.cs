using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>
/// Transport for analysis jobs between the API (producer) and workers (consumer).
/// The in-memory Channel implementation is the local-dev default; a RabbitMQ /
/// Azure Service Bus implementation swaps in for production without touching the
/// API or worker.
/// </summary>
public interface IJobQueue
{
    ValueTask EnqueueAsync(VideoJob job, CancellationToken ct = default);

    /// <summary>Long-running stream of jobs for a worker to process.</summary>
    IAsyncEnumerable<VideoJob> DequeueAllAsync(CancellationToken ct);
}
