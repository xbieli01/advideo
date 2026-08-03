using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Api.Contracts;

/// <summary>Request to register + enqueue a video for analysis.</summary>
public sealed record RegisterVideoRequest(
    string VideoId,
    string VideoUri,
    string? Analyzer = null,
    string? Track = null);

public sealed record JobResponse(
    string VideoId,
    string VideoUri,
    string Analyzer,
    string? Track,
    string Status,
    string? MapKey,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static JobResponse From(VideoJob j) => new(
        j.VideoId, j.VideoUri, j.Analyzer, j.Track, j.Status.ToString(),
        j.MapKey, j.Error, j.CreatedAt, j.UpdatedAt);
}
