using Advideo.AdServing.Matching.Model;

namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>An ad decision handed to the player; events are attributed to it by <see cref="DecisionId"/>.</summary>
public sealed record IssuedDecision(
    string DecisionId,
    string PlatformId,
    string VideoId,
    string CampaignId,
    string? ViewerHash,
    PricingModel Model,
    decimal BidAmount,
    double ShowFromSec,
    DateTimeOffset IssuedAt);

/// <summary>Short-lived log of issued decisions (events arrive seconds to minutes later).</summary>
public interface IDecisionLog
{
    Task RecordAsync(IssuedDecision decision, CancellationToken ct = default);
    Task<IssuedDecision?> GetAsync(string decisionId, CancellationToken ct = default);
}

public sealed record EventOutcome(bool Accepted, bool Duplicate, decimal Cost);

/// <summary>Viewer-side counters for one video (by hashed viewer id).</summary>
public sealed record ViewerState(int AdsInVideo, int AdsToday, double? LastAdAtSec, string? LastCampaignId)
{
    public static readonly ViewerState Empty = new(0, 0, null, null);
}

public sealed record CampaignStats(
    string CampaignId,
    long Impressions,
    long Viewables,
    long Clicks,
    long Closes,
    double Ctr,
    decimal SpentTotal,
    decimal SpentToday);

/// <summary>
/// Server-side delivery counters: spend per pricing model, campaign stats, viewer frequency.
/// Feeds <see cref="DeliveryContext"/> so budgets, caps and pacing use real numbers.
/// </summary>
public interface IDeliveryLedger
{
    Task<EventOutcome> RecordAsync(IssuedDecision decision, AdEventType type, double? atSec, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>States of many campaigns in one call (one round trip for remote stores).</summary>
    Task<IReadOnlyDictionary<string, CampaignDeliveryState>> CampaignStatesAsync(
        IReadOnlyCollection<string> campaignIds, string? viewerHash, string videoId, DateTimeOffset now, CancellationToken ct = default);

    Task<ViewerState> ViewerAsync(string? viewerHash, string videoId, DateTimeOffset now, CancellationToken ct = default);
    Task<CampaignStats> StatsAsync(string campaignId, DateTimeOffset now, CancellationToken ct = default);
}
