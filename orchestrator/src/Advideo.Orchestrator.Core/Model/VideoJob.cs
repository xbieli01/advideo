namespace Advideo.Orchestrator.Core.Model;

/// <summary>Status of a video analysis job through the pipeline.</summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    /// <summary>Final failure ("dead letter"): permanent error or retries exhausted. Re-run via POST /videos/{id}/retry.</summary>
    Failed,
    /// <summary>An attempt failed with a transient error; the job is re-queued at <see cref="VideoJob.NextAttemptAt"/>.</summary>
    Retrying
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

    /// <summary>Language packs for speech/OCR, e.g. "sk,cs,en" (schema/languages). Null = pipeline default.</summary>
    public string? Languages { get; init; }

    public JobStatus Status { get; init; } = JobStatus.Queued;

    /// <summary>Key of the produced content map in the object store, once completed.</summary>
    public string? MapKey { get; init; }

    public string? Error { get; init; }

    /// <summary>Number of processing attempts started so far (0 = never picked up).</summary>
    public int Attempts { get; init; }

    /// <summary>When a <see cref="JobStatus.Retrying"/> job is due to be re-queued.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
