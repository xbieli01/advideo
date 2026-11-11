using System.Text;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.AdServing;

namespace Advideo.Orchestrator.Tests;

file sealed class CountingStore : IMapStore
{
    public int Opens;
    public Task<string> SaveAsync(string videoId, Stream mapJson, CancellationToken ct = default) => Task.FromResult(KeyOf(videoId));
    public Task<bool> ExistsAsync(string mapKey, CancellationToken ct = default) => Task.FromResult(true);
    public string KeyOf(string videoId) => videoId + ".json";
    public Task<Stream?> OpenAsync(string mapKey, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Opens);
        var id = mapKey.Replace(".json", "");
        var json = $$"""{"schemaVersion":"1.0","videoId":"{{id}}","durationSec":5,"generatedAt":"2026-11-11T00:00:00Z","segments":[]}""";
        return Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }
}

file sealed class NoJobs : IJobRepository
{
    public Task UpsertAsync(VideoJob job, CancellationToken ct = default) => Task.CompletedTask;
    public Task<VideoJob?> GetAsync(string videoId, CancellationToken ct = default) => Task.FromResult<VideoJob?>(null);
    public Task<IReadOnlyList<VideoJob>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VideoJob>>([]);
}

file sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public class ContentMapCacheTests
{
    [Fact]
    public async Task Repeated_reads_hit_cache_and_invalidate_forces_reload()
    {
        var store = new CountingStore();
        var cache = new CachedContentMapProvider(store, new NoJobs(), new ContentMapCacheOptions());
        Assert.Equal("v1", (await cache.GetAsync("v1"))!.VideoId);
        await cache.GetAsync("v1");
        Assert.Equal(1, store.Opens);
        cache.Invalidate("v1");
        await cache.GetAsync("v1");
        Assert.Equal(2, store.Opens);
    }

    [Fact]
    public async Task Least_recently_used_entry_is_evicted()
    {
        var store = new CountingStore();
        var cache = new CachedContentMapProvider(store, new NoJobs(), new ContentMapCacheOptions { Capacity = 2 });
        await cache.GetAsync("a");
        await cache.GetAsync("b");
        await cache.GetAsync("a");     // a is now most recent
        await cache.GetAsync("c");     // evicts b
        Assert.Equal(2, cache.Count);
        await cache.GetAsync("a");
        Assert.Equal(3, store.Opens);  // a still cached
        await cache.GetAsync("b");
        Assert.Equal(4, store.Opens);  // b was evicted
    }

    [Fact]
    public async Task Expired_entry_is_reloaded()
    {
        var store = new CountingStore();
        var time = new ManualTime();
        var cache = new CachedContentMapProvider(store, new NoJobs(), new ContentMapCacheOptions { TimeToLive = TimeSpan.FromMinutes(10) }, time);
        await cache.GetAsync("v");
        time.Now = time.Now.AddMinutes(11);
        await cache.GetAsync("v");
        Assert.Equal(2, store.Opens);
    }

    [Fact]
    public async Task Concurrent_misses_share_one_load()
    {
        var store = new CountingStore();
        var cache = new CachedContentMapProvider(store, new NoJobs(), new ContentMapCacheOptions());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync("same")));
        Assert.Equal(1, store.Opens);
    }
}
