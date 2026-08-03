using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.Storage;

public sealed class FileSystemMapStoreOptions
{
    public string RootPath { get; set; } = "./data/maps";
}

/// <summary>Local-filesystem content-map store for dev. Key = "&lt;videoId&gt;.video-map.json".</summary>
public sealed class FileSystemMapStore : IMapStore
{
    private readonly string _root;

    public FileSystemMapStore(FileSystemMapStoreOptions options)
    {
        _root = options.RootPath;
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string mapKey) => Path.Combine(_root, mapKey);

    public static string KeyFor(string videoId) => $"{videoId}.video-map.json";

    public async Task<string> SaveAsync(string videoId, Stream mapJson, CancellationToken ct = default)
    {
        var key = KeyFor(videoId);
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await mapJson.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream?> OpenAsync(string mapKey, CancellationToken ct = default)
    {
        var path = PathFor(mapKey);
        Stream? s = File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(s);
    }

    public Task<bool> ExistsAsync(string mapKey, CancellationToken ct = default)
        => Task.FromResult(File.Exists(PathFor(mapKey)));
}
