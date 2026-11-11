using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Api.Worker;

/// <summary>
/// Consumes analysis jobs from the queue, runs the pipeline, stores the produced
/// content map, and updates job status. Hosted in-process for M1; the same class
/// runs as a standalone worker service when the queue becomes RabbitMQ/Service
/// Bus. Bounded concurrency so it doesn't oversubscribe the GPU workers.
/// </summary>
public sealed class AnalysisWorker : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly IPipelineRunner _runner;
    private readonly IMapStore _store;
    private readonly IJobRepository _repo;
    private readonly ILogger<AnalysisWorker> _log;
    private readonly SemaphoreSlim _gate;
    private readonly IContentMapProvider? _maps;

    public AnalysisWorker(
        IJobQueue queue, IPipelineRunner runner, IMapStore store,
        IJobRepository repo, ILogger<AnalysisWorker> log,
        WorkerOptions? options = null,
        IContentMapProvider? maps = null)
    {
        _maps = maps;
        _queue = queue;
        _runner = runner;
        _store = store;
        _repo = repo;
        _log = log;
        _gate = new SemaphoreSlim(options?.MaxConcurrency ?? 2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("AnalysisWorker started (maxConcurrency={N})", _gate.CurrentCount);
        var running = new List<Task>();
        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            running.Add(ProcessAsync(job, stoppingToken));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    private async Task ProcessAsync(VideoJob job, CancellationToken ct)
    {
        try
        {
            await _repo.UpsertAsync(job with { Status = JobStatus.Running }, ct);
            await using var map = await _runner.RunAsync(job, ct);
            var key = await _store.SaveAsync(job.VideoId, map, ct);
            await _repo.UpsertAsync(job with { Status = JobStatus.Completed, MapKey = key }, ct);
            _maps?.Invalidate(job.VideoId); // re-analysis -> ad decisions use the new map
            _log.LogInformation("Completed {VideoId} -> {Key}", job.VideoId, key);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning("Cancelled {VideoId}", job.VideoId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed {VideoId}", job.VideoId);
            await _repo.UpsertAsync(job with { Status = JobStatus.Failed, Error = ex.Message },
                CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class WorkerOptions
{
    public int MaxConcurrency { get; set; } = 2;
}
