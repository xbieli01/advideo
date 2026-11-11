using Advideo.AdServing.Matching;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Api.Security;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Api.AdServing;

/// <summary>M2 HTTP surface: platform + campaign admin, ad decision and explain.</summary>
public static class AdServingEndpoints
{
    public const string NoAdReasonHeader = "X-Advideo-NoAd-Reason";

    public static IEndpointRouteBuilder MapAdServing(this IEndpointRouteBuilder app)
    {
        // management: reads need "reporting" (or admin), writes need "admin"
        MapPlatforms(app.MapGroup("/platforms").WithTags("Platforms").RequireAuthorization(ApiKeyDefaults.ReportingPolicy));
        MapCampaigns(app.MapGroup("/campaigns").WithTags("Campaigns").RequireAuthorization(ApiKeyDefaults.ReportingPolicy));

        // public snippet endpoints: anonymous, CORS-restricted, rate-limited per client
        var ads = app.MapGroup("/ads").WithTags("Ads").RequireCors("snippet").RequireRateLimiting("ads");
        MapAds(ads);
        MapEvents(ads);
        return app;
    }

    // ---------------------------------------------------------------- platforms
    private static void MapPlatforms(RouteGroupBuilder g)
    {
        g.MapGet("", async (IPlatformRepository repo) => Results.Ok(await repo.ListAsync()));

        g.MapGet("/{id}", async (string id, IPlatformRepository repo) =>
            await repo.GetAsync(id) is { } p ? Results.Ok(p) : Results.NotFound());

        g.MapPost("", async (PlatformConfig p, IPlatformRepository repo) =>
        {
            if (ValidatePlatform(p) is { Count: > 0 } errors) return Results.BadRequest(new ValidationErrorResponse(errors));
            if (await repo.GetAsync(p.Id) is not null) return Results.Conflict(new ValidationErrorResponse([$"Platform '{p.Id}' already exists."]));
            await repo.UpsertAsync(p);
            return Results.Created($"/platforms/{p.Id}", p);
        }).RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        g.MapPut("/{id}", async (string id, PlatformConfig p, IPlatformRepository repo) =>
        {
            p = p with { Id = id };
            if (ValidatePlatform(p) is { Count: > 0 } errors) return Results.BadRequest(new ValidationErrorResponse(errors));
            await repo.UpsertAsync(p);
            return Results.Ok(p);
        }).RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        g.MapDelete("/{id}", async (string id, IPlatformRepository repo) =>
            await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(ApiKeyDefaults.AdminPolicy);
    }

    // ---------------------------------------------------------------- campaigns
    private static void MapCampaigns(RouteGroupBuilder g)
    {
        g.MapGet("", async (ICampaignRepository repo) => Results.Ok(await repo.ListAsync()));

        g.MapGet("/{id}", async (string id, ICampaignRepository repo) =>
            await repo.GetAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        g.MapPost("", async (Campaign c, ICampaignRepository repo, Taxonomy tax) =>
        {
            if (tax.Validate(c) is { Count: > 0 } errors) return Results.BadRequest(new ValidationErrorResponse(errors));
            if (await repo.GetAsync(c.Id) is not null) return Results.Conflict(new ValidationErrorResponse([$"Campaign '{c.Id}' already exists."]));
            await repo.UpsertAsync(c);
            return Results.Created($"/campaigns/{c.Id}", c);
        }).RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        g.MapPut("/{id}", async (string id, Campaign c, ICampaignRepository repo, Taxonomy tax) =>
        {
            c = c with { Id = id };
            if (tax.Validate(c) is { Count: > 0 } errors) return Results.BadRequest(new ValidationErrorResponse(errors));
            await repo.UpsertAsync(c);
            return Results.Ok(c);
        }).RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        g.MapGet("/{id}/stats", async (string id, ICampaignRepository repo, IDeliveryLedger ledger, TimeProvider time) =>
            await repo.GetAsync(id) is null ? Results.NotFound() : Results.Ok(await ledger.StatsAsync(id, time.GetUtcNow())));

        g.MapPost("/{id}/pause", (string id, ICampaignRepository repo) => SetStatus(id, CampaignStatus.Paused, repo))
            .RequireAuthorization(ApiKeyDefaults.AdminPolicy);
        g.MapPost("/{id}/resume", (string id, ICampaignRepository repo) => SetStatus(id, CampaignStatus.Active, repo))
            .RequireAuthorization(ApiKeyDefaults.AdminPolicy);

        g.MapDelete("/{id}", async (string id, ICampaignRepository repo) =>
            await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(ApiKeyDefaults.AdminPolicy);
    }

    private static async Task<IResult> SetStatus(string id, CampaignStatus status, ICampaignRepository repo)
    {
        if (await repo.GetAsync(id) is not { } c) return Results.NotFound();
        c = c with { Status = status };
        await repo.UpsertAsync(c);
        return Results.Ok(c);
    }

    // ---------------------------------------------------------------- ads
    private static void MapAds(RouteGroupBuilder g)
    {
        g.MapGet("/decision", async ([AsParameters] AdQuery q, AdDecisionService svc, HttpResponse res, CancellationToken ct) =>
        {
            var r = await svc.DecideAsync(q, issue: true, ct);
            if (r.PlatformMissing) return Results.NotFound(new ValidationErrorResponse([$"Unknown platform '{q.Platform}'."]));
            if (r.Decision.Winner is not { } w)
            {
                res.Headers[NoAdReasonHeader] = r.Decision.NoAdReason ?? "No ad.";
                return Results.NoContent();
            }
            var seg = r.Segment!;
            return Results.Ok(new AdDecisionResponse(
                DecisionId: r.DecisionId!,
                PlatformId: q.Platform,
                VideoId: q.VideoId,
                T: q.T,
                Segment: new SegmentRef(seg.StartSec, seg.EndSec),
                Ad: new AdPayload(w.Campaign.Id, w.Campaign.AdvertiserId, w.Creative!, w.Slot!, Math.Max(seg.StartSec, q.T), seg.EndSec, r.WinningBid!.Model),
                MatchedOn: w.MatchedOn,
                Tracking: new TrackingInfo("/ads/events", [AdEventType.Impression, AdEventType.Viewable, AdEventType.Click, AdEventType.Close])));
        });

        g.MapGet("/explain", async ([AsParameters] AdQuery q, AdDecisionService svc, CancellationToken ct) =>
        {
            var r = await svc.DecideAsync(q, issue: false, ct);
            if (r.PlatformMissing) return Results.NotFound(new ValidationErrorResponse([$"Unknown platform '{q.Platform}'."]));
            var d = r.Decision;
            return Results.Ok(new ExplainResponse(
                q.Platform, q.VideoId, q.T,
                r.Segment is { } s ? new SegmentRef(s.StartSec, s.EndSec) : null,
                d.Winner?.Campaign.Id,
                d.NoAdReason,
                d.Candidates.Select(c => new ExplainCandidate(
                    c.Campaign.Id, Math.Round(c.Relevance, 4), c.Ecpm, Math.Round(c.Rank, 4), c.MatchedOn,
                    c.Creative?.Id, c.Slot?.Id, c.RejectedBecause)).ToList()));
        }).RequireAuthorization(ApiKeyDefaults.AdminPolicy); // reveals bids and targeting
    }

    private static void MapEvents(RouteGroupBuilder g)
    {
        // Accepts application/json and text/plain: browsers send beacons (navigator.sendBeacon) as
        // text/plain so the cross-origin request stays CORS-simple (no preflight).
        g.MapPost("/events", async (HttpRequest req, IDecisionLog log, IDeliveryLedger ledger, TimeProvider time,
            Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json) =>
        {
            AdEventRequest? e;
            try
            {
                e = await System.Text.Json.JsonSerializer.DeserializeAsync<AdEventRequest>(req.Body, json.Value.SerializerOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                e = null;
            }
            if (e is null || string.IsNullOrWhiteSpace(e.DecisionId))
                return Results.BadRequest(new ValidationErrorResponse(["Body must be JSON {decisionId, type, t}."]));
            if (await log.GetAsync(e.DecisionId) is not { } d)
                return Results.NotFound(new ValidationErrorResponse([$"Unknown or expired decision '{e.DecisionId}'."]));
            var o = await ledger.RecordAsync(d, e.Type, e.T, time.GetUtcNow());
            return Results.Accepted(value: new AdEventResponse(o.Accepted, o.Duplicate, o.Cost));
        });
    }

    private static List<string> ValidatePlatform(PlatformConfig p)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Id)) errors.Add("Platform id is required.");
        if (p.Slots.Count == 0) errors.Add("At least one banner slot is required.");
        if (p.Slots.Any(s => s.Width <= 0 || s.Height <= 0)) errors.Add("Slot size must be positive.");
        if (p.Slots.GroupBy(s => s.Id).Any(x => x.Count() > 1)) errors.Add("Duplicate slot id.");
        if (p.Pricing.EnabledModels.Count == 0) errors.Add("At least one pricing model must be enabled.");
        if (p.Matching.RelevanceThreshold is < 0 or > 1) errors.Add("Relevance threshold must be between 0 and 1.");
        if (p.Frequency.MinIntervalSec < 0 || p.Frequency.MaxPerVideo < 0) errors.Add("Frequency limits must not be negative.");
        return errors;
    }
}
