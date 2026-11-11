namespace Advideo.AdServing.Matching.Model;

public enum CampaignStatus { Draft, Active, Paused, Ended }

/// <summary>How the advertiser pays. CPM is the primary model; others are enabled per platform.</summary>
public enum PricingModel
{
    /// <summary>Cost per 1 000 impressions.</summary>
    Cpm,
    /// <summary>Cost per click.</summary>
    Cpc,
    /// <summary>Cost per viewable impression (banner visible ≥ platform threshold).</summary>
    Cpv,
    /// <summary>Flat fee sponsorship for the campaign period (guaranteed delivery).</summary>
    Flat,
}

public sealed record Bid(PricingModel Model, decimal Amount);

public sealed record Budget(decimal? Total = null, decimal? Daily = null);

public sealed record CampaignFrequencyCap(int? PerViewerPerVideo = null, int? PerViewerPerDay = null);

public sealed record EntityTarget(string Type, string Label);

public sealed record Targeting
{
    public IReadOnlyList<string> Brands { get; init; } = [];
    public IReadOnlyList<EntityTarget> Entities { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> ExcludeCategories { get; init; } = [];
    public IReadOnlyList<string> ExcludeKeywords { get; init; } = [];
    /// <summary>Detections below this confidence are ignored for this campaign.</summary>
    public double MinScore { get; init; }
    /// <summary>Empty = all platforms.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public IReadOnlyList<string> IncludeVideos { get; init; } = [];
    public IReadOnlyList<string> ExcludeVideos { get; init; } = [];
    /// <summary>ISO 3166-1 alpha-2; empty = all countries.</summary>
    public IReadOnlyList<string> Geo { get; init; } = [];
}

public sealed record Creative(
    string Id,
    string Url,
    string ClickUrl,
    int Width,
    int Height,
    string? AltText = null);

public sealed record Campaign
{
    public required string Id { get; init; }
    public required string AdvertiserId { get; init; }
    public required string Name { get; init; }
    public CampaignStatus Status { get; init; } = CampaignStatus.Draft;
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    /// <summary>Agency priority 1–10 (5 = neutral).</summary>
    public int Priority { get; init; } = 5;
    /// <summary>One bid per pricing model; only models enabled on the platform are used.</summary>
    public IReadOnlyList<Bid> Bids { get; init; } = [];
    public Budget Budget { get; init; } = new();
    public CampaignFrequencyCap FrequencyCap { get; init; } = new();
    /// <summary>Optional competitive exclusivity key (e.g. "automotive"): two campaigns with the same key are not shown back to back in one video.</summary>
    public string? ExclusivityKey { get; init; }
    public Targeting Targeting { get; init; } = new();
    public IReadOnlyList<Creative> Creatives { get; init; } = [];
}
