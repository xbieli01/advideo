using System.Text.Json;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.AdServing;

public sealed class ContentMapCacheOptions
{
    /// <summary>Max number of parsed maps kept in memory (LRU eviction).</summary>
    public int Capacity { get; set; } = 1000;
    /// <summary>Entries older than this are reloaded (safety net if a re-analysis bypasses Invalidate).</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// LRU + TTL cache of parsed content maps over <see cref="IMapStore"/>, so an ad decision does not
/// touch the object store. Map key comes from the job (if known), otherwise the store's key convention.
/// Concurrent misses for the same video share one load.
/// </summary>
public sealed class CachedContentMapProvider(
    IMapStore store,
    IJobRepository jobs,
    ContentMapCacheOptions options,
    TimeProvider? time = null) : IContentMapProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new(); // first = most recently used

    private sealed record Entry(string VideoId, Task<ContentMap?> Map, DateTimeOffset LoadedAt);

    public int Count { get { lock (_lock) return _index.Count; } }

    public Task<ContentMap?> GetAsync(string videoId, CancellationToken ct = default)
    {
        Task<ContentMap?> load;
        lock (_lock)
        {
            if (_index.TryGetValue(videoId, out var node))
            {
                var fresh = _time.GetUtcNow() - node.Value.LoadedAt < options.TimeToLive;
                var failed = node.Value.Map.IsFaulted || node.Value.Map.IsCanceled;
                if (fresh && !failed)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Map;
                }
                _lru.Remove(node);
                _index.Remove(videoId);
            }

            load = LoadAsync(videoId, ct);
            var added = _lru.AddFirst(new Entry(videoId, load, _time.GetUtcNow()));
            _index[videoId] = added;
            while (_index.Count > options.Capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _index.Remove(last.Value.VideoId);
            }
        }
        return load;
    }

    public void Invalidate(string videoId)
    {
        lock (_lock)
        {
            if (_index.Remove(videoId, out var node)) _lru.Remove(node);
        }
    }

    private async Task<ContentMap?> LoadAsync(string videoId, CancellationToken ct)
    {
        var key = (await jobs.GetAsync(videoId, ct))?.MapKey ?? store.KeyOf(videoId);
        await using var stream = await store.OpenAsync(key, ct);
        if (stream is null) return null;
        return await JsonSerializer.DeserializeAsync<ContentMap>(stream, Json, ct);
    }
}
