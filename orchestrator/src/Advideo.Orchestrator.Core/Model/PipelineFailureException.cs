namespace Advideo.Orchestrator.Core.Model;

/// <summary>
/// A pipeline run failed. <see cref="Transient"/> drives the worker's retry policy:
/// transient (network, 5xx, timeout, crash) is retried with backoff; permanent (bad input:
/// file not found, 4xx, not a video, too large) goes straight to <see cref="JobStatus.Failed"/>.
/// </summary>
public sealed class PipelineFailureException(string message, bool transient, int? exitCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? ExitCode { get; } = exitCode;
}
