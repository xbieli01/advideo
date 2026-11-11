namespace Advideo.AdServing.Matching.Model;

/// <summary>Where advideo runs: a customer's video portal, or the universal browser extension.</summary>
public enum PlatformKind { Portal, BrowserExtension }

/// <summary>Banner position relative to the video player.</summary>
public enum SlotPosition { Above, Below, Left, Right }

public sealed record AdSlot(string Id, SlotPosition Position, int Width, int Height);

public sealed record PricingSettings
{
    public IReadOnlyList<PricingModel> EnabledModels { get; init; } = [PricingModel.Cpm];
    /// <summary>Expected click-through rate used to convert CPC bids to eCPM until real stats exist.</summary>
    public double DefaultCtr { get; init; } = 0.002;
    /// <summary>Expected viewable rate used to convert CPV bids to eCPM.</summary>
    public double DefaultViewableRate { get; init; } = 0.6;
    /// <summary>eCPM assigned to flat-fee sponsorships so they win when they match.</summary>
    public decimal FlatSponsorshipEcpm { get; init; } = 1_000m;
    public string Currency { get; init; } = "EUR";
}

public sealed record FrequencySettings
{
    public double MinIntervalSec { get; init; } = 60;
    public int MaxPerVideo { get; init; } = 3;
    public int? MaxPerViewerPerDay { get; init; }
}

public sealed record BrandSafetySettings
{
    public IReadOnlyList<string> ExcludeCategories { get; init; } = [];
    public IReadOnlyList<string> ExcludeKeywords { get; init; } = [];
}

public sealed record MatchWeights
{
    public double Brand { get; init; } = 1.0;
    public double Entity { get; init; } = 0.8;
    public double Keyword { get; init; } = 0.6;
    public double Category { get; init; } = 0.4;
}

public sealed record MatchingSettings
{
    /// <summary>Below this relevance no ad is shown (better nothing than an unrelated ad).</summary>
    public double RelevanceThreshold { get; init; } = 0.3;
    public MatchWeights Weights { get; init; } = new();
}

/// <summary>Per-deployment configuration: pricing, banner placement, frequency, brand safety, matching.</summary>
public sealed record PlatformConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PlatformKind Kind { get; init; } = PlatformKind.Portal;
    public PricingSettings Pricing { get; init; } = new();
    public IReadOnlyList<AdSlot> Slots { get; init; } = [];
    public FrequencySettings Frequency { get; init; } = new();
    public BrandSafetySettings BrandSafety { get; init; } = new();
    public MatchingSettings Matching { get; init; } = new();
}
