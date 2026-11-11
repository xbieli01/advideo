using Advideo.AdServing.Matching.Model;

namespace Advideo.AdServing.Matching.Tests;

internal static class Fx
{
    public static readonly Taxonomy Tax = Taxonomy.LoadDefault();
    public static readonly AdMatcher Matcher = new(Tax);
    public static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public static PlatformConfig Platform(params PricingModel[] models) => new()
    {
        Id = "portal-a",
        Name = "Portal A",
        Pricing = new() { EnabledModels = models.Length == 0 ? [PricingModel.Cpm] : models },
        Slots = [new AdSlot("below", SlotPosition.Below, 728, 90), new AdSlot("right", SlotPosition.Right, 300, 250)],
    };

    public static Campaign Campaign(string id, Targeting t, decimal cpm = 5m, int priority = 5, IReadOnlyList<Bid>? bids = null,
        IReadOnlyList<Creative>? creatives = null) => new()
    {
        Id = id,
        AdvertiserId = "adv-" + id,
        Name = id,
        Status = CampaignStatus.Active,
        Priority = priority,
        Bids = bids ?? [new Bid(PricingModel.Cpm, cpm)],
        Targeting = t,
        Creatives = creatives ?? [new Creative(id + "-728", "https://cdn/x.png", "https://adv/x", 728, 90)],
    };

    public static ContentSegment Segment(
        IReadOnlyList<ContentEntity>? entities = null,
        IReadOnlyList<ScoredTerm>? keywords = null,
        IReadOnlyList<string>? categories = null,
        double start = 120, double end = 125, SegmentSignals? signals = null) =>
        new(start, end, keywords ?? [], entities ?? [], categories ?? [], signals);

    public static readonly ContentSegment TeslaOnRoad = Segment(
        entities: [new ContentEntity("brand", "Tesla", 0.9)],
        keywords: [new ScoredTerm("car", 0.8), new ScoredTerm("road", 0.7)],
        categories: ["automotive"]);

    public static DeliveryContext Ctx(Func<DeliveryContext, DeliveryContext>? f = null)
    {
        var c = new DeliveryContext { Now = Noon, Geo = "SK" };
        return f is null ? c : f(c);
    }
}
