using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>
/// The .NET -> Python boundary (ADR-001). Runs the analysis pipeline for a job
/// and returns the produced content-map JSON as a stream. The default
/// implementation shells out to the `advideo-analyze` CLI; alternatives could
/// call an HTTP worker or a gRPC service.
/// </summary>
public interface IPipelineRunner
{
    Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default);
}
