using System.Diagnostics;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Microsoft.Extensions.Logging;

namespace Advideo.Orchestrator.Infrastructure.Pipeline;

public sealed class CliPipelineRunnerOptions
{
    /// <summary>Executable that runs the analysis (the Python CLI entrypoint).</summary>
    public string Command { get; set; } = "advideo-analyze";
    /// <summary>Seconds before a single analysis is abandoned.</summary>
    public int TimeoutSeconds { get; set; } = 3600;
}

/// <summary>
/// Runs the Python pipeline as a subprocess (ADR-001 .NET -> Python boundary):
/// `advideo-analyze --video &lt;uri&gt; --analyzer ... [--track ...] --out &lt;tmp&gt;`,
/// then returns the produced JSON as a stream. Non-zero exit -> exception with
/// captured stderr so the worker can mark the job Failed.
/// </summary>
public sealed class CliPipelineRunner : IPipelineRunner
{
    private readonly CliPipelineRunnerOptions _opt;
    private readonly ILogger<CliPipelineRunner> _log;

    public CliPipelineRunner(CliPipelineRunnerOptions opt, ILogger<CliPipelineRunner> log)
    {
        _opt = opt;
        _log = log;
    }

    public async Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"advideo-{job.VideoId}-{Guid.NewGuid():N}.json");

        var psi = new ProcessStartInfo
        {
            FileName = _opt.Command,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--video"); psi.ArgumentList.Add(job.VideoUri);
        psi.ArgumentList.Add("--video-id"); psi.ArgumentList.Add(job.VideoId);
        psi.ArgumentList.Add("--analyzer"); psi.ArgumentList.Add(job.Analyzer);
        if (!string.IsNullOrWhiteSpace(job.Track))
        {
            psi.ArgumentList.Add("--track"); psi.ArgumentList.Add(job.Track);
        }
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outPath);

        using var proc = new Process { StartInfo = psi };
        _log.LogInformation("Running pipeline for {VideoId}: {Cmd} {Args}",
            job.VideoId, _opt.Command, string.Join(' ', psi.ArgumentList));

        proc.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"pipeline exited {proc.ExitCode} for {job.VideoId}: {stderr}");

        if (!File.Exists(outPath))
            throw new InvalidOperationException($"pipeline produced no output for {job.VideoId}");

        // read into memory then delete the temp file, so the caller owns the stream
        var bytes = await File.ReadAllBytesAsync(outPath, ct);
        TryDelete(outPath);
        return new MemoryStream(bytes, writable: false);
    }

    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
