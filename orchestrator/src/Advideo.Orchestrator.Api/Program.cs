using Advideo.Orchestrator.Api.Contracts;
using Advideo.Orchestrator.Api.Worker;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.Pipeline;
using Advideo.Orchestrator.Infrastructure.Queue;
using Advideo.Orchestrator.Infrastructure.Repository;
using Advideo.Orchestrator.Infrastructure.Storage;
using Amazon.S3;

var builder = WebApplication.CreateBuilder(args);

// --- core singletons ---------------------------------------------------------
builder.Services.AddSingleton<IJobRepository, InMemoryJobRepository>();
builder.Services.AddSingleton<IJobQueue>(_ => new ChannelJobQueue(
    builder.Configuration.GetValue("Queue:Capacity", 1000)));

// --- pipeline runner (.NET -> Python) ---------------------------------------
builder.Services.AddSingleton(new CliPipelineRunnerOptions
{
    Command = builder.Configuration.GetValue("Pipeline:Command", "advideo-analyze")!,
    TimeoutSeconds = builder.Configuration.GetValue("Pipeline:TimeoutSeconds", 3600)
});
builder.Services.AddSingleton<IPipelineRunner, CliPipelineRunner>();

// --- content-map store: filesystem (default) or S3/MinIO --------------------
var storageProvider = builder.Configuration.GetValue("Storage:Provider", "filesystem")!;
if (storageProvider.Equals("s3", StringComparison.OrdinalIgnoreCase))
{
    var s3Opt = new S3MapStoreOptions();
    builder.Configuration.GetSection("Storage:S3").Bind(s3Opt);
    builder.Services.AddSingleton(s3Opt);
    builder.Services.AddSingleton<IAmazonS3>(_ =>
    {
        var cfg = new AmazonS3Config { ForcePathStyle = s3Opt.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(s3Opt.ServiceUrl)) cfg.ServiceURL = s3Opt.ServiceUrl;
        return new AmazonS3Client(cfg);
    });
    builder.Services.AddSingleton<IMapStore, S3MapStore>();
}
else
{
    var fsOpt = new FileSystemMapStoreOptions
    {
        RootPath = builder.Configuration.GetValue("Storage:FileSystem:RootPath", "./data/maps")!
    };
    builder.Services.AddSingleton(fsOpt);
    builder.Services.AddSingleton<IMapStore>(_ => new FileSystemMapStore(fsOpt));
}

// --- worker ------------------------------------------------------------------
builder.Services.AddSingleton(new WorkerOptions
{
    MaxConcurrency = builder.Configuration.GetValue("Worker:MaxConcurrency", 2)
});
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();

// --- endpoints ---------------------------------------------------------------
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/videos", async (RegisterVideoRequest req, IJobRepository repo, IJobQueue queue) =>
{
    if (string.IsNullOrWhiteSpace(req.VideoId) || string.IsNullOrWhiteSpace(req.VideoUri))
        return Results.BadRequest(new { error = "videoId and videoUri are required" });

    var job = new VideoJob
    {
        VideoId = req.VideoId,
        VideoUri = req.VideoUri,
        Analyzer = string.IsNullOrWhiteSpace(req.Analyzer) ? "yolo+clip" : req.Analyzer!,
        Track = req.Track,
        Status = JobStatus.Queued
    };
    await repo.UpsertAsync(job);
    await queue.EnqueueAsync(job);
    return Results.Accepted($"/videos/{job.VideoId}", JobResponse.From(job));
});

app.MapGet("/videos", async (IJobRepository repo) =>
    Results.Ok((await repo.ListAsync()).Select(JobResponse.From)));

app.MapGet("/videos/{videoId}", async (string videoId, IJobRepository repo) =>
{
    var job = await repo.GetAsync(videoId);
    return job is null ? Results.NotFound() : Results.Ok(JobResponse.From(job));
});

app.MapGet("/videos/{videoId}/map", async (string videoId, IJobRepository repo, IMapStore store) =>
{
    var job = await repo.GetAsync(videoId);
    if (job?.MapKey is null) return Results.NotFound();
    var stream = await store.OpenAsync(job.MapKey);
    return stream is null ? Results.NotFound() : Results.Stream(stream, "application/json");
});

app.Run();

/// <summary>Exposed so integration tests can spin up the app via WebApplicationFactory.</summary>
public partial class Program;
