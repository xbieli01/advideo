using Advideo.AdServing.Matching.Model;

namespace Advideo.AdServing.Matching;

/// <summary>Normalizes bids of different pricing models to eCPM so they can be ranked together.</summary>
public static class Pricing
{
    /// <summary>Best eCPM among the campaign's bids whose model is enabled on the platform; null if none.</summary>
    public static (decimal Ecpm, Bid Bid)? BestBid(Campaign campaign, PlatformConfig platform, CampaignDeliveryState state)
    {
        (decimal, Bid)? best = null;
        foreach (var bid in campaign.Bids)
        {
            if (!platform.Pricing.EnabledModels.Contains(bid.Model)) continue;
            var ecpm = Ecpm(bid, platform.Pricing, state);
            if (best is null || ecpm > best.Value.Item1) best = (ecpm, bid);
        }
        return best;
    }

    public static decimal Ecpm(Bid bid, PricingSettings p, CampaignDeliveryState state) => bid.Model switch
    {
        PricingModel.Cpm => bid.Amount,
        PricingModel.Cpc => bid.Amount * (decimal)ObservedCtr(p, state) * 1000m,
        PricingModel.Cpv => bid.Amount * (decimal)p.DefaultViewableRate * 1000m,
        PricingModel.Flat => p.FlatSponsorshipEcpm,
        _ => 0m,
    };

    /// <summary>Real CTR once the campaign has enough impressions, otherwise the platform default.</summary>
    private static double ObservedCtr(PricingSettings p, CampaignDeliveryState s) =>
        s.Impressions >= 1_000 ? Math.Max((double)s.Clicks / s.Impressions, 0.0001) : p.DefaultCtr;

    /// <summary>
    /// What a delivery event costs the advertiser: CPM charges per impression (amount / 1000),
    /// CPV per viewable impression, CPC per click. Flat-fee sponsorships are prepaid (0 per event).
    /// </summary>
    public static decimal CostOf(PricingModel model, decimal amount, AdEventType type) => (model, type) switch
    {
        (PricingModel.Cpm, AdEventType.Impression) => amount / 1000m,
        (PricingModel.Cpv, AdEventType.Viewable) => amount,
        (PricingModel.Cpc, AdEventType.Click) => amount,
        _ => 0m,
    };
}
