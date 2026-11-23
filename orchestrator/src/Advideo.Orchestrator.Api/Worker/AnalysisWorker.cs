using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Api.Worker;

/// <summary>
/// Consumes analysis jobs from the queue, runs the pipeline, stores the produced
/// content map, and updates job status. Hosted in-process for M1; the same class
/// runs as a standalone worker service when the queue becomes RabbitMQ/Service
/// Bus. Bounded concurrency so it doesn't oversubscribe the GPU workers.
/// <para>
/// Robustness (R5): transient failures (network, 5xx, timeouts, crashes) are retried with
/// exponential backoff up to <see cref="WorkerOptions.MaxAttempts"/>; permanent failures
/// (bad input) and exhausted retries end in <see cref="JobStatus.Failed"/> (the dead-letter
/// state, re-run via <c>POST /videos/{id}/retry</c>). On start-up, unfinished jobs from the
/// repository are re-queued so a restart does not lose work.
/// </para>
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
    private readonly WorkerOptions _opt;
    private readonly TimeProvider _time;
    private CancellationToken _stopping;

    public AnalysisWorker(
        IJobQueue queue, IPipelineRunner runner, IMapStore store,
        IJobRepository repo, ILogger<AnalysisWorker> log,
        WorkerOptions? options = null,
        IContentMapProvider? maps = null,
        TimeProvider? time = null)
    {
        _maps = maps;
        _queue = queue;
        _runner = runner;
        _store = store;
        _repo = repo;
        _log = log;
        _opt = options ?? new WorkerOptions();
        _time = time ?? TimeProvider.System;
        _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _log.LogInformation("AnalysisWorker started (maxConcurrency={N}, maxAttempts={A})",
            _gate.CurrentCount, _opt.MaxAttempts);

        await RecoverAsync(stoppingToken);

        var running = new List<Task>();
        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            running.Add(ProcessAsync(job, stoppingToken));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    /// <summary>Re-queue jobs a previous process left unfinished (needs a persistent repository).</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            var now = _time.GetUtcNow();
            var recovered = 0;
            foreach (var job in await _repo.ListAsync(ct))
            {
                switch (job.Status)
                {
                    case JobStatus.Queued:
                        await _queue.EnqueueAsync(job, ct);
                        recovered++;
                        break;
                    case JobStatus.Running: // interrupted mid-run: the attempt did not finish
                        var requeued = job with { Status = JobStatus.Queued, Attempts = Math.Max(0, job.Attempts - 1) };
                        await _repo.UpsertAsync(requeued, ct);
                        await _queue.EnqueueAsync(requeued, ct);
                        recovered++;
                        break;
                    case JobStatus.Retrying:
                        var due = job.NextAttemptAt ?? now;
                        Schedule(job, due > now ? due - now : TimeSpan.Zero);
                        recovered++;
                        break;
                }
            }
            if (recovered > 0) _log.LogInformation("Recovered {N} unfinished job(s)", recovered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Job recovery failed; continuing with new jobs only");
        }
    }

    private async Task ProcessAsync(VideoJob queued, CancellationToken ct)
    {
        VideoJob? running = null;
        try
        {
            // the repository is the source of truth: skip stale queue entries (re-registered,
            // already processed, or manually retried in the meantime)
            var current = await _repo.GetAsync(queued.VideoId, ct);
            if (current is null || current.Status is not (JobStatus.Queued or JobStatus.Retrying)
                || current.VideoUri != queued.VideoUri)
            {
                _log.LogDebug("Skipping stale queue entry for {VideoId}", queued.VideoId);
                return;
            }

            running = current with { Status = JobStatus.Running, Attempts = current.Attempts + 1, NextAttemptAt = null };
            await _repo.UpsertAsync(running, ct);

            await using var map = await _runner.RunAsync(running, ct);
            var key = await _store.SaveAsync(running.VideoId, map, ct);
            await _repo.UpsertAsync(running with { Status = JobStatus.Completed, MapKey = key, Error = null }, ct);
            _maps?.Invalidate(running.VideoId); // re-analysis -> ad decisions use the new map
            _log.LogInformation("Completed {VideoId} -> {Key} (attempt {N})", running.VideoId, key, running.Attempts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning("Cancelled {VideoId}", queued.VideoId);
            if (running is not null) // put it back so the next start picks it up; the attempt does not count
                await TryUpsert(running with { Status = JobStatus.Queued, Attempts = running.Attempts - 1 });
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(running ?? queued, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleFailureAsync(VideoJob job, Exception ex)
    {
        var transient = ex is not PipelineFailureException p || p.Transient;
        var attempt = Math.Max(1, job.Attempts);

        if (!transient || attempt >= _opt.MaxAttempts)
        {
            var reason = transient ? $"gave up after {attempt} attempt(s): {ex.Message}" : $"permanent error: {ex.Message}";
            _log.LogError(ex, "Failed {VideoId} ({Reason})", job.VideoId, transient ? "retries exhausted" : "permanent");
            await TryUpsert(job with { Status = JobStatus.Failed, Error = reason, NextAttemptAt = null });
            return;
        }

        var delay = RetryDelay(attempt);
        var next = job with
        {
            Status = JobStatus.Retrying,
            Error = ex.Message,
            NextAttemptAt = _time.GetUtcNow() + delay
        };
        _log.LogWarning("Attempt {N}/{Max} failed for {VideoId}, retrying in {Delay}: {Error}",
            attempt, _opt.MaxAttempts, job.VideoId, delay, ex.Message);
        await TryUpsert(next);
        Schedule(next, delay);
    }

    /// <summary>Exponential backoff: base · 2^(attempt-1), capped.</summary>
    public TimeSpan RetryDelay(int attempt)
    {
        var seconds = _opt.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, _opt.RetryMaxDelaySeconds));
    }

    private void Schedule(VideoJob job, TimeSpan delay)
    {
        var stopping = _stopping;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, stopping);
                await _queue.EnqueueAsync(job, stopping);
            }
            catch (OperationCanceledException)
            {
                // shutting down: the job stays Retrying in the repository and is recovered on next start
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not re-queue {VideoId}", job.VideoId);
            }
        }, CancellationToken.None);
    }

    private async Task TryUpsert(VideoJob job)
    {
        try { await _repo.UpsertAsync(job, CancellationToken.None); }
        catch (Exception ex) { _log.LogError(ex, "Could not update job {VideoId}", job.VideoId); }
    }
}

public sealed class WorkerOptions
{
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Attempts per job including the first one; transient failures are retried until this is reached.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry; doubles with each further attempt.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>Upper bound for the backoff delay.</summary>
    public double RetryMaxDelaySeconds { get; set; } = 900;
}
