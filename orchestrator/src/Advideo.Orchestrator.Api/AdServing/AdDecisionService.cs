using Advideo.AdServing.Matching;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Advideo.Orchestrator.Api.AdServing;

/// <summary>
/// Query of the player snippet. <c>viewer</c> is an anonymous first-party id (hashed server-side);
/// optional client counters (<c>lastAdAt</c>, <c>adsInVideo</c>, <c>prevCampaign</c>) are combined with
/// server-side counters, the stricter value wins.
/// </summary>
public sealed record AdQuery(
    [FromQuery(Name = "platform")] string Platform,
    [FromQuery(Name = "videoId")] string VideoId,
    [FromQuery(Name = "t")] double T,
    [FromQuery(Name = "viewer")] string? Viewer = null,
    [FromQuery(Name = "geo")] string? Geo = null,
    [FromQuery(Name = "lastAdAt")] double? LastAdAt = null,
    [FromQuery(Name = "adsInVideo")] int AdsInVideo = 0,
    [FromQuery(Name = "prevCampaign")] string? PrevCampaign = null);

public sealed record DecisionResult(
    bool PlatformMissing,
    ContentSegment? Segment,
    AdDecision Decision,
    Bid? WinningBid,
    string? DecisionId);

/// <summary>Loads platform, campaigns, map (cached) and delivery counters, then runs the pure matcher.</summary>
public sealed class AdDecisionService(
    IPlatformRepository platforms,
    ICampaignRepository campaigns,
    IContentMapProvider maps,
    IDeliveryLedger ledger,
    IDecisionLog decisions,
    ViewerIdHasher viewerHasher,
    AdMatcher matcher,
    TimeProvider time)
{
    public async Task<DecisionResult> DecideAsync(AdQuery q, bool issue, CancellationToken ct)
    {
        var platform = await platforms.GetAsync(q.Platform, ct);
        if (platform is null) return new(true, null, new AdDecision(null, [], "Unknown platform."), null, null);

        var now = time.GetUtcNow();
        var viewerHash = viewerHasher.Hash(q.Viewer);
        var map = await maps.GetAsync(q.VideoId, ct);
        var segment = map?.FindSegment(q.T);
        var servable = await campaigns.ListServableAsync(platform.Id, ct);

        var viewer = await ledger.ViewerAsync(viewerHash, q.VideoId, now, ct);
        var prevCampaign = q.PrevCampaign ?? viewer.LastCampaignId;
        string? prevKey = prevCampaign is null ? null : (await campaigns.GetAsync(prevCampaign, ct))?.ExclusivityKey;

        var states = await ledger.CampaignStatesAsync(servable.Select(c => c.Id).ToList(), viewerHash, q.VideoId, now, ct);

        var ctx = new DeliveryContext
        {
            Now = now,
            Geo = q.Geo,
            PlaybackTimeSec = q.T,
            LastAdAtSec = Latest(q.LastAdAt, viewer.LastAdAtSec),
            AdsShownInVideo = Math.Max(q.AdsInVideo, viewer.AdsInVideo),
            AdsShownToViewerToday = viewer.AdsToday,
            PreviousCampaignId = prevCampaign,
            PreviousExclusivityKey = prevKey,
            Campaigns = states,
        };

        var decision = map is null
            ? new AdDecision(null, [], "No content map for this video.")
            : matcher.Decide(q.VideoId, segment, servable, platform, ctx);

        if (decision.Winner is not { } w) return new(false, segment, decision, null, null);

        var bid = Pricing.BestBid(w.Campaign, platform, ctx.StateOf(w.Campaign.Id))!.Value.Bid;
        string? decisionId = null;
        if (issue)
        {
            decisionId = "dcs_" + Guid.NewGuid().ToString("N");
            await decisions.RecordAsync(new IssuedDecision(decisionId, platform.Id, q.VideoId, w.Campaign.Id, viewerHash,
                bid.Model, bid.Amount, Math.Max(segment!.StartSec, q.T), now), ct);
        }
        return new(false, segment, decision, bid, decisionId);
    }

    /// <summary>The more recent ad time is the stricter one for the minimum-interval rule.</summary>
    private static double? Latest(double? a, double? b) => (a, b) switch
    {
        (null, null) => null,
        ({ } x, null) => x,
        (null, { } y) => y,
        ({ } x, { } y) => Math.Max(x, y),
    };
}
