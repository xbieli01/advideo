using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Advideo.Orchestrator.Api.Worker;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.Queue;
using Advideo.Orchestrator.Infrastructure.Repository;
using Advideo.Orchestrator.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Advideo.Orchestrator.Tests;

/// <summary>
/// Pipeline whose behaviour is scripted by the video URI:
/// "/scripted/transient-N/…" fails N times with a transient error, then succeeds;
/// "/scripted/permanent/…" always fails permanently; "/scripted/crash/…" always throws a plain exception.
/// </summary>
internal sealed class ScriptedPipelineRunner : IPipelineRunner
{
    public readonly ConcurrentDictionary<string, int> Calls = new();

    public Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default)
    {
        var n = Calls.AddOrUpdate(job.VideoId, 1, (_, c) => c + 1);
        var uri = job.VideoUri.Replace("/scripted/", "");
        if (uri.StartsWith("permanent"))
            throw new PipelineFailureException("HTTP 404 for source", transient: false, exitCode: 3);
        if (uri.StartsWith("crash"))
            throw new InvalidOperationException("worker crashed");
        if (uri.StartsWith("transient-"))
        {
            var failures = int.Parse(uri["transient-".Length..uri.IndexOf('/')]);
            if (n <= failures) throw new PipelineFailureException("HTTP 503 for source", transient: true, exitCode: 4);
        }
        var json = $$"""{"schemaVersion":"1.0","videoId":"{{job.VideoId}}","durationSec":12,"generatedAt":"2026-11-23T00:00:00Z","segments":[]}""";
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }
}

public class RetryDeadLetterTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ScriptedPipelineRunner _runner = new();

    public RetryDeadLetterTests(WebApplicationFactory<Program> factory)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "advideo-retry-" + Guid.NewGuid().ToString("N"));
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.WithTestKeys();
            b.UseSetting("Storage:FileSystem:RootPath", dataDir);
            b.UseSetting("Worker:MaxAttempts", "3");
            b.UseSetting("Worker:RetryBaseDelaySeconds", "0.05");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IPipelineRunner>();
                s.AddSingleton<IPipelineRunner>(_runner);
            });
        });
    }

    private static async Task<JobDto> WaitFor(HttpClient c, string id, params string[] statuses)
    {
        JobDto? job = null;
        for (var i = 0; i < 100; i++)
        {
            job = await c.GetFromJsonAsync<JobDto>($"/videos/{id}");
            if (job is not null && statuses.Contains(job.status)) return job;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"{id} did not reach {string.Join('/', statuses)}; last={job?.status} error={job?.error}");
    }

    [Fact]
    public async Task Transient_failures_are_retried_until_success()
    {
        var c = _factory.AdminClient();
        (await c.PostAsJsonAsync("/videos", new { videoId = "t2", videoUri = "/scripted/transient-2/v.mp4", analyzer = "mock" }))
            .EnsureSuccessStatusCode();

        var job = await WaitFor(c, "t2", "Completed", "Failed");
        Assert.Equal("Completed", job.status);
        Assert.Equal(3, job.attempts);
        Assert.Null(job.error);
        Assert.Equal(3, _runner.Calls["t2"]);
    }

    [Fact]
    public async Task Retries_exhausted_end_in_failed_dead_letter()
    {
        var c = _factory.AdminClient();
        await c.PostAsJsonAsync("/videos", new { videoId = "t9", videoUri = "/scripted/transient-9/v.mp4", analyzer = "mock" });

        var job = await WaitFor(c, "t9", "Completed", "Failed");
        Assert.Equal("Failed", job.status);
        Assert.Equal(3, job.attempts);
        Assert.Contains("gave up after 3 attempt", job.error);

        var dead = await c.GetFromJsonAsync<List<JobDto>>("/videos?status=failed");
        Assert.Contains(dead!, j => j.videoId == "t9");
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        var c = _factory.AdminClient();
        await c.PostAsJsonAsync("/videos", new { videoId = "p1", videoUri = "/scripted/permanent/missing.mp4", analyzer = "mock" });

        var job = await WaitFor(c, "p1", "Failed");
        Assert.Equal(1, job.attempts);
        Assert.StartsWith("permanent error", job.error);
        Assert.Equal(1, _runner.Calls["p1"]);
    }

    [Fact]
    public async Task Unexpected_exceptions_count_as_transient()
    {
        var c = _factory.AdminClient();
        await c.PostAsJsonAsync("/videos", new { videoId = "c1", videoUri = "/scripted/crash/x.mp4", analyzer = "mock" });
        var job = await WaitFor(c, "c1", "Failed");
        Assert.Equal(3, job.attempts);
    }

    [Fact]
    public async Task Retry_endpoint_requeues_failed_job()
    {
        var c = _factory.AdminClient();
        await c.PostAsJsonAsync("/videos", new { videoId = "r1", videoUri = "/scripted/transient-3/v.mp4", analyzer = "mock" });
        await WaitFor(c, "r1", "Failed"); // 3 attempts used, 3 failures scripted

        var resp = await c.PostAsync("/videos/r1/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var job = await WaitFor(c, "r1", "Completed");
        Assert.Equal(1, job.attempts); // attempts reset by the manual retry
    }

    [Fact]
    public async Task Retry_endpoint_conflict_and_not_found()
    {
        var c = _factory.AdminClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/videos/nope/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/videos?status=bogus")).StatusCode);
    }

    private sealed record JobDto(string videoId, string status, string? error, int attempts, DateTimeOffset? nextAttemptAt);
}

/// <summary>Worker-level tests without the web host (recovery and backoff).</summary>
public class AnalysisWorkerRecoveryTests
{
    [Fact]
    public async Task Unfinished_jobs_are_recovered_on_start()
    {
        var repo = new InMemoryJobRepository();
        var queue = new ChannelJobQueue();
        await repo.UpsertAsync(new VideoJob { VideoId = "q", VideoUri = "/v/q.mp4", Analyzer = "mock" });
        await repo.UpsertAsync(new VideoJob { VideoId = "r", VideoUri = "/v/r.mp4", Analyzer = "mock", Status = JobStatus.Running, Attempts = 1 });
        await repo.UpsertAsync(new VideoJob { VideoId = "w", VideoUri = "/v/w.mp4", Analyzer = "mock", Status = JobStatus.Retrying, Attempts = 1, NextAttemptAt = DateTimeOffset.UtcNow });
        await repo.UpsertAsync(new VideoJob { VideoId = "done", VideoUri = "/v/d.mp4", Analyzer = "mock", Status = JobStatus.Completed, MapKey = "k" });

        var dir = Path.Combine(Path.GetTempPath(), "advideo-rec-" + Guid.NewGuid().ToString("N"));
        var worker = new AnalysisWorker(queue, new OkRunner(), new FileSystemMapStore(new FileSystemMapStoreOptions { RootPath = dir }),
            repo, NullLogger<AnalysisWorker>.Instance, new WorkerOptions { RetryBaseDelaySeconds = 0 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        for (var i = 0; i < 100 && (await repo.ListAsync()).Any(j => j.Status != JobStatus.Completed); i++)
            await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        var jobs = (await repo.ListAsync()).ToDictionary(j => j.VideoId);
        Assert.All(jobs.Values, j => Assert.Equal(JobStatus.Completed, j.Status));
        Assert.Equal(1, jobs["r"].Attempts); // interrupted attempt not counted twice
        Assert.Equal("k", jobs["done"].MapKey); // untouched
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(10, 900)]
    public void Backoff_doubles_and_is_capped(int attempt, double expectedSeconds)
    {
        var worker = new AnalysisWorker(new ChannelJobQueue(), new OkRunner(), null!, new InMemoryJobRepository(),
            NullLogger<AnalysisWorker>.Instance, new WorkerOptions());
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), worker.RetryDelay(attempt));
    }

    private sealed class OkRunner : IPipelineRunner
    {
        public Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{"schemaVersion":"1.0","videoId":"{{job.VideoId}}","durationSec":1,"generatedAt":"2026-11-23T00:00:00Z","segments":[]}""")));
    }
}

/// <summary>Exit-code classification of the real CLI runner (uses a shell script as the "pipeline").</summary>
public class CliPipelineRunnerExitCodeTests
{
    private static string Script(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"advideo-fake-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static Advideo.Orchestrator.Infrastructure.Pipeline.CliPipelineRunner Runner(string cmd, int timeout = 30) =>
        new(new Advideo.Orchestrator.Infrastructure.Pipeline.CliPipelineRunnerOptions { Command = cmd, TimeoutSeconds = timeout },
            NullLogger<Advideo.Orchestrator.Infrastructure.Pipeline.CliPipelineRunner>.Instance);

    private static readonly VideoJob Job = new() { VideoId = "x", VideoUri = "https://cdn/x.mp4", Analyzer = "mock" };

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(1, true)]
    public async Task Exit_code_maps_to_transient_flag(int code, bool transient)
    {
        if (OperatingSystem.IsWindows()) return;
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() =>
            Runner(Script($"echo 'SOURCE_ERROR boom' >&2; exit {code}")).RunAsync(Job));
        Assert.Equal(transient, ex.Transient);
        Assert.Equal(code, ex.ExitCode);
        Assert.Contains("SOURCE_ERROR boom", ex.Message);
    }

    [Fact]
    public async Task Timeout_is_transient()
    {
        if (OperatingSystem.IsWindows()) return;
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => Runner(Script("sleep 10"), timeout: 1).RunAsync(Job));
        Assert.True(ex.Transient);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Missing_command_is_permanent()
    {
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => Runner("/nonexistent/advideo-analyze").RunAsync(Job));
        Assert.False(ex.Transient);
    }
}
