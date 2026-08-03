namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>
/// Object store for produced JSON content maps. Filesystem implementation for
/// local dev; S3-compatible (MinIO / AWS, via AWSSDK.S3) for production — the
/// same storage the platform's client SDK later reads maps from.
/// </summary>
public interface IMapStore
{
    /// <summary>Store the map JSON under a key derived from the video id; returns the key.</summary>
    Task<string> SaveAsync(string videoId, Stream mapJson, CancellationToken ct = default);

    /// <summary>Open the stored map for reading, or null if absent.</summary>
    Task<Stream?> OpenAsync(string mapKey, CancellationToken ct = default);

    Task<bool> ExistsAsync(string mapKey, CancellationToken ct = default);
}
