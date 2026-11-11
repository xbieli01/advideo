using Advideo.AdServing.Matching;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Advideo.Orchestrator.Api.AdServing;

/// <summary>
/// Query of the player snippet. Viewer-side counters (last ad time, ads shown) are sent by the snippet
/// for now; server-side delivery counters (budget, caps) come in T-005c.
/// </summary>
public sealed record AdQuery(
    [FromQuery(Name = "platform")] string Platform,
    [FromQuery(Name = "videoId")] string VideoId,
    [FromQuery(Name = "t")] double T,
    [FromQuery(Name = "geo")] string? Geo = null,
    [FromQuery(Name = "lastAdAt")] double? LastAdAt = null,
    [FromQuery(Name = "adsInVideo")] int AdsInVideo = 0,
    [FromQuery(Name = "prevCampaign")] string? PrevCampaign = null);

public sealed record DecisionResult(bool PlatformMissing, ContentSegment? Segment, AdDecision Decision, PricingModel? WinningModel);

/// <summary>Loads platform, campaigns and map (cached), builds the delivery context and runs the pure matcher.</summary>
public sealed class AdDecisionService(
    IPlatformRepository platforms,
    ICampaignRepository campaigns,
    IContentMapProvider maps,
    AdMatcher matcher,
    TimeProvider time)
{
    public async Task<DecisionResult> DecideAsync(AdQuery q, CancellationToken ct)
    {
        var platform = await platforms.GetAsync(q.Platform, ct);
        if (platform is null) return new(true, null, new AdDecision(null, [], "Unknown platform."), null);

        var map = await maps.GetAsync(q.VideoId, ct);
        var segment = map?.FindSegment(q.T);
        var servable = await campaigns.ListServableAsync(platform.Id, ct);

        string? prevKey = null;
        if (q.PrevCampaign is not null)
            prevKey = (await campaigns.GetAsync(q.PrevCampaign, ct))?.ExclusivityKey;

        var ctx = new DeliveryContext
        {
            Now = time.GetUtcNow(),
            Geo = q.Geo,
            LastAdAtSec = q.LastAdAt,
            AdsShownInVideo = q.AdsInVideo,
            PreviousCampaignId = q.PrevCampaign,
            PreviousExclusivityKey = prevKey,
        };

        var decision = map is null
            ? new AdDecision(null, [], "No content map for this video.")
            : matcher.Decide(q.VideoId, segment, servable, platform, ctx);

        PricingModel? model = decision.Winner is { } w
            ? Pricing.BestBid(w.Campaign, platform, ctx.StateOf(w.Campaign.Id))?.Bid.Model
            : null;
        return new(false, segment, decision, model);
    }
}
