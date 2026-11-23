using System.Net;
using System.Net.Http.Json;
using System.Text;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Advideo.Orchestrator.Tests;

internal sealed class OkPipelineRunner : IPipelineRunner
{
    public int Calls;
    public Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":"1.0","videoId":"{{job.VideoId}}","durationSec":5,"generatedAt":"2026-11-23T00:00:00Z","segments":[]}""")));
    }
}

public class BulkImportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly OkPipelineRunner _runner = new();

    public BulkImportTests(WebApplicationFactory<Program> factory)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "advideo-bulk-" + Guid.NewGuid().ToString("N"));
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.WithTestKeys();
            b.UseSetting("Storage:FileSystem:RootPath", dataDir);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IPipelineRunner>();
                s.AddSingleton<IPipelineRunner>(_runner);
            });
        });
    }

    private sealed record ItemResult(int index, string? videoId, string reason);
    private sealed record BulkResult(int received, int queued, List<string> queuedIds, List<ItemResult> skipped, List<ItemResult> invalid);
    private sealed record JobDto(string videoId, string status, string analyzer, string? track);

    private static async Task WaitAllCompleted(HttpClient c, params string[] ids)
    {
        for (var i = 0; i < 100; i++)
        {
            var jobs = await c.GetFromJsonAsync<List<JobDto>>("/videos");
            if (ids.All(id => jobs!.Any(j => j.videoId == id && j.status == "Completed"))) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("jobs did not complete");
    }

    [Fact]
    public async Task Imports_valid_items_and_reports_invalid_ones()
    {
        var c = _factory.AdminClient();
        var resp = await c.PostAsJsonAsync("/videos/bulk", new
        {
            analyzer = "mock",
            items = new object[]
            {
                new { videoId = "cat-1", videoUri = "https://cdn.example.com/cat-1.mp4" },
                new { videoId = "cat-2", videoUri = "https://cdn.example.com/show/index.m3u8", track = "whisper" },
                new { videoId = "cat-1", videoUri = "https://cdn.example.com/dup.mp4" },   // duplicate in batch
                new { videoId = "../etc/passwd", videoUri = "/x.mp4" },                    // unsafe id
                new { videoId = "cat-4", videoUri = "ftp://host/x.mp4" },                  // bad scheme
                new { videoId = "cat-5" },                                                 // missing uri
            }
        });
        resp.EnsureSuccessStatusCode();
        var r = (await resp.Content.ReadFromJsonAsync<BulkResult>())!;

        Assert.Equal(6, r.received);
        Assert.Equal(2, r.queued);
        Assert.Equal(new[] { "cat-1", "cat-2" }, r.queuedIds);
        Assert.Equal(new[] { 2, 3, 4, 5 }, r.invalid.Select(x => x.index));
        Assert.Contains("duplicate", r.invalid[0].reason);

        await WaitAllCompleted(c, "cat-1", "cat-2");
        var cat2 = await c.GetFromJsonAsync<JobDto>("/videos/cat-2");
        Assert.Equal("mock", cat2!.analyzer);   // batch default
        Assert.Equal("whisper", cat2.track);    // item override
    }

    [Fact]
    public async Task Reimport_is_idempotent_unless_forced()
    {
        var c = _factory.AdminClient();
        var items = new[] { new { videoId = "idem-1", videoUri = "https://cdn.example.com/idem-1.mp4", analyzer = "mock" } };
        (await c.PostAsJsonAsync("/videos/bulk", new { items })).EnsureSuccessStatusCode();
        await WaitAllCompleted(c, "idem-1");
        var callsAfterFirst = _runner.Calls;

        var again = (await (await c.PostAsJsonAsync("/videos/bulk", new { items })).Content.ReadFromJsonAsync<BulkResult>())!;
        Assert.Equal(0, again.queued);
        Assert.Contains("already analyzed", again.skipped.Single().reason);

        var changedUri = new[] { new { videoId = "idem-1", videoUri = "https://cdn.example.com/idem-1-v2.mp4", analyzer = "mock" } };
        var changed = (await (await c.PostAsJsonAsync("/videos/bulk", new { items = changedUri })).Content.ReadFromJsonAsync<BulkResult>())!;
        Assert.Equal(1, changed.queued); // new source -> re-analysis

        await WaitAllCompleted(c, "idem-1");
        var forced = (await (await c.PostAsJsonAsync("/videos/bulk", new { items = changedUri, force = true })).Content.ReadFromJsonAsync<BulkResult>())!;
        Assert.Equal(1, forced.queued);
        await WaitAllCompleted(c, "idem-1");
        Assert.True(_runner.Calls >= callsAfterFirst + 2);
    }

    [Fact]
    public async Task Rejects_empty_and_oversized_batches()
    {
        var c = _factory.AdminClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/videos/bulk", new { items = Array.Empty<object>() })).StatusCode);
        var many = Enumerable.Range(0, 1001).Select(i => new { videoId = $"v{i}", videoUri = $"/v/{i}.mp4" }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/videos/bulk", new { items = many })).StatusCode);
    }

    [Fact]
    public async Task Single_register_rejects_unsafe_id()
    {
        var c = _factory.AdminClient();
        var resp = await c.PostAsJsonAsync("/videos", new { videoId = "../../evil", videoUri = "/x.mp4" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bulk_requires_admin_key()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/videos/bulk", new { items = new[] { new { videoId = "a", videoUri = "/a.mp4" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
