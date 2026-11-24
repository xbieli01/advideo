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
    /// <summary>Language packs for jobs without their own (e.g. "sk,cs,en"); empty = pipeline default (taxonomy / ADVIDEO_LANGUAGES).</summary>
    public string? DefaultLanguages { get; set; }
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
        var languages = string.IsNullOrWhiteSpace(job.Languages) ? _opt.DefaultLanguages : job.Languages;
        if (!string.IsNullOrWhiteSpace(languages))
        {
            psi.ArgumentList.Add("--languages"); psi.ArgumentList.Add(languages);
        }
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outPath);

        using var proc = new Process { StartInfo = psi };
        _log.LogInformation("Running pipeline for {VideoId}: {Cmd} {Args}",
            job.VideoId, _opt.Command, string.Join(' ', psi.ArgumentList));

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PipelineFailureException(
                $"cannot start pipeline command '{_opt.Command}': {ex.Message}", transient: false, inner: ex);
        }
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
            TryDelete(outPath);
            if (ct.IsCancellationRequested) throw; // host shutdown / caller cancel
            throw new PipelineFailureException(
                $"pipeline timed out after {_opt.TimeoutSeconds}s for {job.VideoId}", transient: true);
        }

        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            TryDelete(outPath);
            // exit codes of advideo-analyze: 3 = permanent source error, 4 = transient source error, other = crash
            var permanent = proc.ExitCode == PermanentExitCode;
            throw new PipelineFailureException(
                $"pipeline exited {proc.ExitCode} for {job.VideoId}: {Tail(stderr)}",
                transient: !permanent, exitCode: proc.ExitCode);
        }

        if (!File.Exists(outPath))
            throw new PipelineFailureException($"pipeline produced no output for {job.VideoId}", transient: true);

        // read into memory then delete the temp file, so the caller owns the stream
        var bytes = await File.ReadAllBytesAsync(outPath, ct);
        TryDelete(outPath);
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>advideo-analyze exit code for bad input that a retry cannot fix.</summary>
    public const int PermanentExitCode = 3;

    private static string Tail(string s, int max = 2000) =>
        s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();

    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
