using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.AdServing.Matching;
using Advideo.Orchestrator.Api.AdServing;
using Advideo.Orchestrator.Api.Contracts;
using Advideo.Orchestrator.Api.Security;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Advideo.Orchestrator.Api.Worker;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.AdServing;
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

// --- ad serving (M2) ----------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ =>
{
    var path = builder.Configuration.GetValue<string>("AdServing:TaxonomyPath");
    return string.IsNullOrWhiteSpace(path) ? Taxonomy.LoadDefault() : Taxonomy.Load(path);
});
builder.Services.AddSingleton<AdMatcher>();
builder.Services.AddSingleton<IPlatformRepository, InMemoryPlatformRepository>();
builder.Services.AddSingleton<ICampaignRepository, InMemoryCampaignRepository>();
var mapCacheOpt = new ContentMapCacheOptions();
builder.Configuration.GetSection("AdServing:MapCache").Bind(mapCacheOpt);
builder.Services.AddSingleton(mapCacheOpt);
builder.Services.AddSingleton<IContentMapProvider, CachedContentMapProvider>();
builder.Services.AddSingleton<IDecisionLog>(sp => new InMemoryDecisionLog(time: sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IDeliveryLedger, InMemoryDeliveryLedger>();
var viewerSecret = builder.Configuration.GetValue<string>("AdServing:ViewerIdSecret");
if (string.IsNullOrWhiteSpace(viewerSecret)) viewerSecret = "advideo-dev-only-secret"; // override in every real deployment
builder.Services.AddSingleton(new ViewerIdHasher(viewerSecret));
builder.Services.AddSingleton<AdDecisionService>();

// CORS for the player snippet: origins of customer portals (and the extension) that may call /ads/*.
var corsOrigins = builder.Configuration.GetSection("AdServing:Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("snippet", p =>
{
    if (corsOrigins.Contains("*")) p.AllowAnyOrigin(); else p.WithOrigins(corsOrigins);
    p.WithMethods("GET", "POST").WithHeaders("Content-Type").WithExposedHeaders(AdServingEndpoints.NoAdReasonHeader);
}));

// --- security: API keys (management), rate limiting (public snippet endpoints) ------
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("Security"));
builder.Services.AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyDefaults.AdminPolicy, p => p.RequireRole("admin"))
    .AddPolicy(ApiKeyDefaults.ReportingPolicy, p => p.RequireRole("admin", "reporting"));

var adsPerMinute = builder.Configuration.GetValue("RateLimit:AdsPerMinutePerClient", 600);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ads", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = adsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // trust is decided by Network:TrustForwardedHeaders (behind a known ingress only)
    o.KnownProxies.Clear();
});
builder.Services.AddSingleton<IReadinessProbe, MapStoreReadinessProbe>();

// --- worker ------------------------------------------------------------------
builder.Services.AddSingleton(new WorkerOptions
{
    MaxConcurrency = builder.Configuration.GetValue("Worker:MaxConcurrency", 2)
});
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();

if (app.Configuration.GetValue("Network:TrustForwardedHeaders", false)) app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiKeyOptions>>().Value.ApiKeys.Count == 0)
    app.Logger.LogWarning("No API keys configured (Security:ApiKeys) — management endpoints are unreachable.");

// --- seed platforms + campaigns (dev/demo) -------------------------------------
var seedPath = app.Configuration.GetValue<string>("AdServing:SeedFile");
if (!string.IsNullOrWhiteSpace(seedPath)) seedPath = Path.GetFullPath(seedPath, app.Environment.ContentRootPath);
if (!string.IsNullOrWhiteSpace(seedPath) && File.Exists(seedPath))
{
    var seed = await AdServingSeed.LoadAsync(seedPath);
    var tax = app.Services.GetRequiredService<Taxonomy>();
    foreach (var c in seed.Campaigns)
        foreach (var e in tax.Validate(c))
            app.Logger.LogWarning("Seed campaign {Id}: {Error}", c.Id, e);
    await seed.ApplyAsync(app.Services.GetRequiredService<IPlatformRepository>(), app.Services.GetRequiredService<ICampaignRepository>());
    app.Logger.LogInformation("Seeded {P} platforms and {C} campaigns from {Path}", seed.Platforms.Count, seed.Campaigns.Count, seedPath);
}

// --- endpoints ---------------------------------------------------------------
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (IEnumerable<IReadinessProbe> probes, CancellationToken ct) =>
{
    var results = new Dictionary<string, string>();
    var ok = true;
    foreach (var p in probes)
    {
        try { await p.CheckAsync(ct); results[p.Name] = "ok"; }
        catch (Exception ex) { ok = false; results[p.Name] = ex.GetType().Name; }
    }
    return Results.Json(new { status = ok ? "ready" : "not-ready", checks = results },
        statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

var videos = app.MapGroup("/videos").RequireAuthorization(ApiKeyDefaults.AdminPolicy);

videos.MapPost("", async (RegisterVideoRequest req, IJobRepository repo, IJobQueue queue) =>
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

videos.MapGet("", async (IJobRepository repo) =>
    Results.Ok((await repo.ListAsync()).Select(JobResponse.From)));

videos.MapGet("/{videoId}", async (string videoId, IJobRepository repo) =>
{
    var job = await repo.GetAsync(videoId);
    return job is null ? Results.NotFound() : Results.Ok(JobResponse.From(job));
});

videos.MapGet("/{videoId}/map", async (string videoId, IJobRepository repo, IMapStore store) =>
{
    var job = await repo.GetAsync(videoId);
    if (job?.MapKey is null) return Results.NotFound();
    var stream = await store.OpenAsync(job.MapKey);
    return stream is null ? Results.NotFound() : Results.Stream(stream, "application/json");
});

app.UseCors();
app.MapAdServing();

app.Run();

/// <summary>Exposed so integration tests can spin up the app via WebApplicationFactory.</summary>
public partial class Program;
