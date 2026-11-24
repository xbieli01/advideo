using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Microsoft.Extensions.Logging;

namespace Advideo.Orchestrator.Infrastructure.Pipeline;

public sealed class WorkerPipelineRunnerOptions
{
    /// <summary>Executable of the long-running Python worker (JSON lines on stdin/stdout).</summary>
    public string Command { get; set; } = "advideo-worker";
    /// <summary>Extra arguments, e.g. "--preload yolo+clip,ocr,whisper" to load models at start-up.</summary>
    public string Arguments { get; set; } = "";
    /// <summary>Worker processes kept warm (match Worker:MaxConcurrency).</summary>
    public int PoolSize { get; set; } = 2;
    /// <summary>Seconds before a single analysis is abandoned (the worker process is then killed and replaced).</summary>
    public int TimeoutSeconds { get; set; } = 3600;
    /// <summary>Restart a worker after this many jobs (bounds memory growth of ML libraries); 0 = never.</summary>
    public int MaxJobsPerWorker { get; set; } = 200;
    /// <summary>Language packs for jobs without their own (see CliPipelineRunnerOptions.DefaultLanguages).</summary>
    public string? DefaultLanguages { get; set; }
}

/// <summary>
/// R4: runs jobs on a pool of warm <c>advideo-worker</c> processes, so models are loaded once per process instead
/// of once per video (the CLI spends ~35 s on imports/model loading on CPU before the first frame). Protocol and
/// error semantics: see <c>pipeline/src/advideo_pipeline/worker.py</c>; errors map to the same
/// <see cref="PipelineFailureException"/> (exit code 3 = permanent, otherwise transient) as <see cref="CliPipelineRunner"/>.
/// A worker that crashes, times out or answers garbage is killed and replaced on the next job.
/// </summary>
public sealed class WorkerPipelineRunner : IPipelineRunner, IAsyncDisposable
{
    private readonly WorkerPipelineRunnerOptions _opt;
    private readonly ILogger<WorkerPipelineRunner> _log;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<WorkerProcess> _idle = [];
    private readonly ConcurrentDictionary<WorkerProcess, byte> _all = new();
    private int _disposed;

    public WorkerPipelineRunner(WorkerPipelineRunnerOptions opt, ILogger<WorkerPipelineRunner> log)
    {
        _opt = opt;
        _log = log;
        _slots = new SemaphoreSlim(Math.Max(1, opt.PoolSize));
    }

    public async Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default)
    {
        await _slots.WaitAsync(ct);
        WorkerProcess? worker = null;
        var healthy = false;
        try
        {
            worker = TakeOrStart();
            var request = new JsonObject
            {
                ["id"] = $"{job.VideoId}-{Guid.NewGuid():N}",
                ["videoUri"] = job.VideoUri,
                ["videoId"] = job.VideoId,
                ["analyzer"] = job.Analyzer,
                ["track"] = job.Track,
                ["languages"] = string.IsNullOrWhiteSpace(job.Languages) ? _opt.DefaultLanguages : job.Languages
            };
            var id = (string)request["id"]!;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
            JsonNode? response;
            try
            {
                response = await worker.CallAsync(request.ToJsonString(), id, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PipelineFailureException(
                    $"pipeline timed out after {_opt.TimeoutSeconds}s for {job.VideoId}", transient: true);
            }
            catch (WorkerProtocolException ex)
            {
                throw new PipelineFailureException(
                    $"pipeline worker failed for {job.VideoId}: {ex.Message}{worker.StderrTail()}", transient: true, inner: ex);
            }
            healthy = true;

            if (response?["ok"]?.GetValue<bool>() == true && response["map"] is JsonNode map)
                return new MemoryStream(Encoding.UTF8.GetBytes(map.ToJsonString()), writable: false);

            var exitCode = response?["exitCode"]?.GetValue<int>() ?? 1;
            var transient = response?["transient"]?.GetValue<bool>() ?? exitCode != CliPipelineRunner.PermanentExitCode;
            throw new PipelineFailureException(
                $"pipeline exited {exitCode} for {job.VideoId}: {response?["error"]?.GetValue<string>() ?? "no error message"}",
                transient: transient, exitCode: exitCode);
        }
        finally
        {
            if (worker is not null)
            {
                if (healthy && !worker.Exhausted(_opt.MaxJobsPerWorker) && Volatile.Read(ref _disposed) == 0)
                    _idle.Add(worker);
                else
                    Retire(worker, healthy ? "recycled" : "failed");
            }
            _slots.Release();
        }
    }

    private WorkerProcess TakeOrStart()
    {
        while (_idle.TryTake(out var w))
        {
            if (w.IsAlive) return w;
            Retire(w, "exited");
        }
        var started = WorkerProcess.Start(_opt.Command, _opt.Arguments, _log);
        _all[started] = 0;
        return started;
    }

    private void Retire(WorkerProcess w, string reason)
    {
        _all.TryRemove(w, out _);
        _log.LogInformation("Pipeline worker {Pid} {Reason} after {Jobs} job(s)", w.Pid, reason, w.Jobs);
        w.Kill();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            foreach (var w in _all.Keys) w.Kill();
        return ValueTask.CompletedTask;
    }

    private sealed class WorkerProtocolException(string message) : Exception(message);

    /// <summary>One <c>advideo-worker</c> process; used by one job at a time.</summary>
    private sealed class WorkerProcess
    {
        private readonly Process _proc;
        private readonly StringBuilder _stderr = new();
        public int Jobs { get; private set; }
        public int Pid { get; }

        private WorkerProcess(Process proc)
        {
            _proc = proc;
            Pid = proc.Id;
        }

        public static WorkerProcess Start(string command, string arguments, ILogger log)
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                proc.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new PipelineFailureException($"cannot start pipeline worker '{command}': {ex.Message}", transient: false, inner: ex);
            }
            var w = new WorkerProcess(proc);
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (w._stderr)
                {
                    w._stderr.AppendLine(e.Data);
                    if (w._stderr.Length > 8000) w._stderr.Remove(0, w._stderr.Length - 4000);
                }
            };
            proc.BeginErrorReadLine();
            log.LogInformation("Started pipeline worker {Pid}: {Cmd} {Args}", w.Pid, command, arguments);
            return w;
        }

        public bool IsAlive { get { try { return !_proc.HasExited; } catch { return false; } } }

        public bool Exhausted(int maxJobs) => maxJobs > 0 && Jobs >= maxJobs;

        public async Task<JsonNode?> CallAsync(string requestJson, string id, CancellationToken ct)
        {
            if (!IsAlive) throw new WorkerProtocolException("worker process is not running");
            Jobs++;
            await _proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);
            while (true)
            {
                string? line;
                try
                {
                    line = await _proc.StandardOutput.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Kill();
                    throw;
                }
                if (line is null) throw new WorkerProtocolException($"worker exited (code {SafeExitCode()})");
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { throw new WorkerProtocolException($"invalid response line: {Trim(line)}"); }
                var rid = node?["id"]?.GetValue<string>();
                if (rid == id) return node;
                // answers for an abandoned request cannot appear (a timed-out worker is killed); anything else is garbage
                throw new WorkerProtocolException($"unexpected response id '{rid}'");
            }
        }

        public string StderrTail()
        {
            lock (_stderr)
            {
                var s = _stderr.ToString().Trim();
                return s.Length == 0 ? "" : " | stderr: " + (s.Length > 1500 ? "…" + s[^1500..] : s);
            }
        }

        public void Kill()
        {
            try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
            try { _proc.Dispose(); } catch { }
        }

        private string SafeExitCode() { try { return _proc.HasExited ? _proc.ExitCode.ToString() : "?"; } catch { return "?"; } }
        private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
    }
}
