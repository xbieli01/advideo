using System.Text;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.Queue;
using Advideo.Orchestrator.Infrastructure.Repository;
using Advideo.Orchestrator.Infrastructure.Storage;

namespace Advideo.Orchestrator.Tests;

public class FileSystemMapStoreTests
{
    [Fact]
    public async Task Save_then_open_roundtrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "advideo-test-" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemMapStore(new FileSystemMapStoreOptions { RootPath = dir });

        var content = """{"schemaVersion":"1.0","videoId":"v1"}""";
        var key = await store.SaveAsync("v1", new MemoryStream(Encoding.UTF8.GetBytes(content)));

        Assert.Equal("v1.video-map.json", key);
        Assert.True(await store.ExistsAsync(key));

        await using var s = await store.OpenAsync(key);
        Assert.NotNull(s);
        using var reader = new StreamReader(s!);
        Assert.Equal(content, await reader.ReadToEndAsync());

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Open_missing_returns_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "advideo-test-" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemMapStore(new FileSystemMapStoreOptions { RootPath = dir });
        Assert.Null(await store.OpenAsync("nope.video-map.json"));
    }
}

public class ChannelJobQueueTests
{
    [Fact]
    public async Task Enqueued_job_is_dequeued()
    {
        var queue = new ChannelJobQueue();
        var job = new VideoJob { VideoId = "v1", VideoUri = "/tmp/v1.mp4" };
        await queue.EnqueueAsync(job);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var got in queue.DequeueAllAsync(cts.Token))
        {
            Assert.Equal("v1", got.VideoId);
            break;
        }
    }
}

public class InMemoryJobRepositoryTests
{
    [Fact]
    public async Task Upsert_updates_status_and_get_returns_latest()
    {
        var repo = new InMemoryJobRepository();
        var job = new VideoJob { VideoId = "v1", VideoUri = "/tmp/v1.mp4" };
        await repo.UpsertAsync(job);
        await repo.UpsertAsync(job with { Status = JobStatus.Completed, MapKey = "v1.video-map.json" });

        var got = await repo.GetAsync("v1");
        Assert.NotNull(got);
        Assert.Equal(JobStatus.Completed, got!.Status);
        Assert.Equal("v1.video-map.json", got.MapKey);
        Assert.Single(await repo.ListAsync());
    }
}
