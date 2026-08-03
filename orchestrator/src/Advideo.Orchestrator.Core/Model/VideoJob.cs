namespace Advideo.Orchestrator.Core.Model;

/// <summary>Status of a video analysis job through the pipeline.</summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

/// <summary>
/// A unit of offline work: analyze one video into a JSON content map.
/// Immutable identity + mutable status tracked in <see cref="Advideo.Orchestrator.Core.Abstractions.IJobRepository"/>.
/// </summary>
public sealed record VideoJob
{
    public required string VideoId { get; init; }

    /// <summary>Local path or URI the worker can read the source video from.</summary>
    public required string VideoUri { get; init; }

    /// <summary>Frame backends, e.g. "yolo+clip,ocr". Passed to the pipeline.</summary>
    public string Analyzer { get; init; } = "yolo+clip";

    /// <summary>Track backends, e.g. "whisper". Optional.</summary>
    public string? Track { get; init; }

    public JobStatus Status { get; init; } = JobStatus.Queued;

    /// <summary>Key of the produced content map in the object store, once completed.</summary>
    public string? MapKey { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
