using System.Text.Json;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Advideo.Orchestrator.Tests;

/// <summary>R4 warm worker pool, tested against a fake worker written in sh (same JSON-lines protocol).</summary>
public class WorkerPipelineRunnerTests
{
    // answers per videoUri: "permanent" -> ok:false exit 3, "transient" -> ok:false exit 4, "crash" -> process exits,
    // "slow" -> never answers, "garbage" -> non-JSON line; otherwise ok:true with the worker PID and languages in the map
    private const string FakeWorker = """
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
          langs=$(printf '%s' "$line" | sed -n 's/.*"languages":"\([^"]*\)".*/\1/p')
          case "$line" in
            *permanent*) printf '{"id":"%s","ok":false,"transient":false,"exitCode":3,"error":"SOURCE_ERROR permanent: 404"}\n' "$id" ;;
            *transient*) printf '{"id":"%s","ok":false,"transient":true,"exitCode":4,"error":"SOURCE_ERROR transient: 503"}\n' "$id" ;;
            *crash*) echo "boom" >&2; exit 1 ;;
            *slow*) sleep 30 ;;
            *garbage*) echo "this is not json" ;;
            *) printf '{"id":"%s","ok":true,"map":{"schemaVersion":"1.0","pid":%s,"languages":"%s","segments":[]}}\n' "$id" "$$" "$langs" ;;
          esac
        done
        """;

    private static string Script()
    {
        var path = Path.Combine(Path.GetTempPath(), $"advideo-fake-worker-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + FakeWorker + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static WorkerPipelineRunner Runner(int pool = 1, int timeout = 30, int maxJobs = 200, string? defaults = null) =>
        new(new WorkerPipelineRunnerOptions { Command = Script(), PoolSize = pool, TimeoutSeconds = timeout, MaxJobsPerWorker = maxJobs, DefaultLanguages = defaults },
            NullLogger<WorkerPipelineRunner>.Instance);

    private static VideoJob Job(string uri, string? languages = null) =>
        new() { VideoId = "v" + Guid.NewGuid().ToString("N")[..6], VideoUri = uri, Analyzer = "mock", Languages = languages };

    private static async Task<JsonElement> Map(WorkerPipelineRunner r, VideoJob job)
    {
        await using var s = await r.RunAsync(job);
        return (await JsonDocument.ParseAsync(s)).RootElement.Clone();
    }

    [Fact]
    public async Task Process_is_reused_across_jobs()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner();
        var a = await Map(r, Job("/v/a.mp4"));
        var b = await Map(r, Job("/v/b.mp4"));
        Assert.Equal(a.GetProperty("pid").GetInt32(), b.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Job_languages_win_over_defaults()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner(defaults: "sk,en");
        Assert.Equal("sk,en", (await Map(r, Job("/v/a.mp4"))).GetProperty("languages").GetString());
        Assert.Equal("hu", (await Map(r, Job("/v/b.mp4", "hu"))).GetProperty("languages").GetString());
    }

    [Theory]
    [InlineData("https://cdn/permanent.mp4", false, 3)]
    [InlineData("https://cdn/transient.mp4", true, 4)]
    public async Task Job_errors_keep_the_worker_and_map_to_exit_codes(string uri, bool transient, int code)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner();
        var before = (await Map(r, Job("/v/a.mp4"))).GetProperty("pid").GetInt32();
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => r.RunAsync(Job(uri)));
        Assert.Equal(transient, ex.Transient);
        Assert.Equal(code, ex.ExitCode);
        Assert.Equal(before, (await Map(r, Job("/v/b.mp4"))).GetProperty("pid").GetInt32());
    }

    [Theory]
    [InlineData("/v/crash.mp4")]
    [InlineData("/v/garbage.mp4")]
    public async Task Broken_worker_is_replaced(string uri)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner();
        var before = (await Map(r, Job("/v/a.mp4"))).GetProperty("pid").GetInt32();
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => r.RunAsync(Job(uri)));
        Assert.True(ex.Transient);
        Assert.NotEqual(before, (await Map(r, Job("/v/b.mp4"))).GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Timeout_kills_the_worker_and_is_transient()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner(timeout: 1);
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => r.RunAsync(Job("/v/slow.mp4")));
        Assert.True(ex.Transient);
        Assert.Contains("timed out", ex.Message);
        Assert.Equal(JsonValueKind.Number, (await Map(r, Job("/v/ok.mp4"))).GetProperty("pid").ValueKind);
    }

    [Fact]
    public async Task Worker_is_recycled_after_max_jobs()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner(maxJobs: 2);
        var p1 = (await Map(r, Job("/v/1.mp4"))).GetProperty("pid").GetInt32();
        var p2 = (await Map(r, Job("/v/2.mp4"))).GetProperty("pid").GetInt32();
        var p3 = (await Map(r, Job("/v/3.mp4"))).GetProperty("pid").GetInt32();
        Assert.Equal(p1, p2);
        Assert.NotEqual(p2, p3);
    }

    [Fact]
    public async Task Pool_runs_jobs_in_parallel_on_separate_processes()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var r = Runner(pool: 2);
        var maps = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Map(r, Job($"/v/{i}.mp4"))));
        var pids = maps.Select(m => m.GetProperty("pid").GetInt32()).Distinct().Count();
        Assert.InRange(pids, 1, 2);
    }

    [Fact]
    public async Task Missing_command_is_permanent()
    {
        await using var r = new WorkerPipelineRunner(new WorkerPipelineRunnerOptions { Command = "/nonexistent/advideo-worker" },
            NullLogger<WorkerPipelineRunner>.Instance);
        var ex = await Assert.ThrowsAsync<PipelineFailureException>(() => r.RunAsync(Job("/v/a.mp4")));
        Assert.False(ex.Transient);
    }
}
