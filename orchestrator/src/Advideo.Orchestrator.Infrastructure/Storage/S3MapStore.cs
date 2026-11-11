using Amazon.S3;
using Amazon.S3.Model;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.Storage;

public sealed class S3MapStoreOptions
{
    public string Bucket { get; set; } = "advideo-maps";
    public string Prefix { get; set; } = "maps/";
    /// <summary>Custom endpoint for MinIO / S3-compatible; null = real AWS.</summary>
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; } = true; // required by MinIO
}

/// <summary>
/// S3-compatible content-map store (AWS S3 or MinIO). Mirrors the storage the
/// client SDK later reads maps from. Auth via the standard AWS credential chain
/// (env / profile / role) — no secrets in code (see secrets convention).
/// </summary>
public sealed class S3MapStore : IMapStore
{
    private readonly IAmazonS3 _s3;
    private readonly S3MapStoreOptions _opt;

    public S3MapStore(IAmazonS3 s3, S3MapStoreOptions options)
    {
        _s3 = s3;
        _opt = options;
    }

    public static string KeyFor(string prefix, string videoId)
        => $"{prefix}{videoId}.video-map.json";

    public string KeyOf(string videoId) => KeyFor(_opt.Prefix, videoId);

    public async Task<string> SaveAsync(string videoId, Stream mapJson, CancellationToken ct = default)
    {
        var key = KeyFor(_opt.Prefix, videoId);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = key,
            InputStream = mapJson,
            ContentType = "application/json",
            AutoCloseStream = false
        }, ct);
        return key;
    }

    public async Task<Stream?> OpenAsync(string mapKey, CancellationToken ct = default)
    {
        try
        {
            var resp = await _s3.GetObjectAsync(_opt.Bucket, mapKey, ct);
            return resp.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string mapKey, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_opt.Bucket, mapKey, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
