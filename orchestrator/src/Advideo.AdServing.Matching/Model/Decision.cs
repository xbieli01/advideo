namespace Advideo.AdServing.Matching.Model;

/// <summary>Live counters for frequency/pacing, provided by the caller (no I/O inside the matcher).</summary>
public sealed record DeliveryContext
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public string? Geo { get; init; }
    /// <summary>Playback time of the last ad shown to this viewer in this video, if any.</summary>
    public double? LastAdAtSec { get; init; }
    public int AdsShownInVideo { get; init; }
    public int AdsShownToViewerToday { get; init; }
    /// <summary>Campaign of the previous ad in this video (for exclusivity).</summary>
    public string? PreviousCampaignId { get; init; }
    /// <summary>Exclusivity key of the previous ad in this video.</summary>
    public string? PreviousExclusivityKey { get; init; }
    public IReadOnlyDictionary<string, CampaignDeliveryState> Campaigns { get; init; } =
        new Dictionary<string, CampaignDeliveryState>();

    public CampaignDeliveryState StateOf(string campaignId) =>
        Campaigns.TryGetValue(campaignId, out var s) ? s : CampaignDeliveryState.Empty;
}

public sealed record CampaignDeliveryState(
    decimal SpentTotal = 0,
    decimal SpentToday = 0,
    int ViewerImpressionsInVideo = 0,
    int ViewerImpressionsToday = 0,
    long Impressions = 0,
    long Clicks = 0)
{
    public static readonly CampaignDeliveryState Empty = new();
}

public enum MatchKind { Brand, Entity, Keyword, Category }

public sealed record MatchHit(MatchKind Kind, string Term, double Score, double Contribution);

public sealed record Candidate(
    Campaign Campaign,
    double Relevance,
    decimal Ecpm,
    double Rank,
    IReadOnlyList<MatchHit> MatchedOn,
    Creative? Creative,
    AdSlot? Slot,
    string? RejectedBecause);

public sealed record AdDecision(
    Candidate? Winner,
    IReadOnlyList<Candidate> Candidates,
    string? NoAdReason)
{
    public bool HasAd => Winner is not null;
}
