using Advideo.AdServing.Matching.Model;
using static Advideo.AdServing.Matching.Tests.Fx;

namespace Advideo.AdServing.Matching.Tests;

public class AdMatcherTests
{
    [Fact]
    public void Brand_match_beats_category_match_at_same_bid()
    {
        var tesla = Campaign("tesla", new Targeting { Brands = ["Tesla"] });
        var autoGeneric = Campaign("auto", new Targeting { Categories = ["automotive"] });

        var d = Matcher.Decide("v1", TeslaOnRoad, [autoGeneric, tesla], Platform(), Ctx());

        Assert.Equal("tesla", d.Winner!.Campaign.Id);
        Assert.Contains(d.Winner.MatchedOn, h => h.Kind == MatchKind.Brand && h.Term == "Tesla");
        Assert.True(d.Candidates.Single(c => c.Campaign.Id == "auto").Rank < d.Winner.Rank);
    }

    [Fact]
    public void Brand_aliases_are_canonicalized_through_shared_taxonomy()
    {
        var vw = Campaign("vw", new Targeting { Brands = ["volkswagen"] });
        var seg = Segment(entities: [new ContentEntity("brand", "vw", 0.8)]);

        var d = Matcher.Decide("v1", seg, [vw], Platform(), Ctx());

        Assert.Equal("vw", d.Winner?.Campaign.Id);
        Assert.Equal("VW", d.Winner!.MatchedOn[0].Term);
    }

    [Fact]
    public void Higher_bid_wins_among_equally_relevant_campaigns()
    {
        var a = Campaign("a", new Targeting { Categories = ["automotive"] }, cpm: 4m);
        var b = Campaign("b", new Targeting { Categories = ["automotive"] }, cpm: 6m);
        Assert.Equal("b", Matcher.Decide("v1", TeslaOnRoad, [a, b], Platform(), Ctx()).Winner!.Campaign.Id);
    }

    [Fact]
    public void Priority_can_outweigh_a_slightly_higher_bid()
    {
        var a = Campaign("a", new Targeting { Categories = ["automotive"] }, cpm: 5m, priority: 10);
        var b = Campaign("b", new Targeting { Categories = ["automotive"] }, cpm: 6m, priority: 5);
        Assert.Equal("a", Matcher.Decide("v1", TeslaOnRoad, [a, b], Platform(), Ctx()).Winner!.Campaign.Id);
    }

    [Fact]
    public void Ties_are_broken_deterministically_by_campaign_id()
    {
        var b = Campaign("b", new Targeting { Categories = ["automotive"] });
        var a = Campaign("a", new Targeting { Categories = ["automotive"] });
        Assert.Equal("a", Matcher.Decide("v1", TeslaOnRoad, [b, a], Platform(), Ctx()).Winner!.Campaign.Id);
    }

    [Fact]
    public void Relevance_below_platform_threshold_shows_no_ad()
    {
        var weak = Campaign("weak", new Targeting { Keywords = ["road"] });
        var seg = Segment(keywords: [new ScoredTerm("road", 0.3)]); // 0.6 * 0.3 = 0.18 < 0.3

        var d = Matcher.Decide("v1", seg, [weak], Platform(), Ctx());

        Assert.False(d.HasAd);
        Assert.Contains("below threshold", d.Candidates[0].RejectedBecause);
    }

    [Fact]
    public void Campaign_min_score_ignores_low_confidence_detections()
    {
        var strict = Campaign("strict", new Targeting { Brands = ["Tesla"], MinScore = 0.95 });
        var d = Matcher.Decide("v1", TeslaOnRoad, [strict], Platform(), Ctx());
        Assert.Equal("No targeting match.", d.Candidates[0].RejectedBecause);
    }

    [Fact]
    public void Ocr_and_audio_text_signals_count_as_keywords()
    {
        var c = Campaign("c", new Targeting { Keywords = ["electric car"] });
        var seg = Segment(signals: new SegmentSignals(AudioKeywords: ["Electric  Car"]), categories: []);
        var d = Matcher.Decide("v1", seg, [c], Platform(), Ctx());
        Assert.Equal("c", d.Winner?.Campaign.Id);
        Assert.Equal(0.3, d.Winner!.Relevance, 3);
    }

    [Fact]
    public void Campaign_exclusions_block_the_campaign()
    {
        var c = Campaign("c", new Targeting { Categories = ["automotive"], ExcludeKeywords = ["road"] });
        var d = Matcher.Decide("v1", TeslaOnRoad, [c], Platform(), Ctx());
        Assert.False(d.HasAd);
        Assert.StartsWith("Campaign exclusion", d.Candidates[0].RejectedBecause);
    }

    [Fact]
    public void Platform_brand_safety_blocks_all_ads()
    {
        var p = Platform() with { BrandSafety = new() { ExcludeCategories = ["automotive"] } };
        var d = Matcher.Decide("v1", TeslaOnRoad, [Campaign("t", new Targeting { Brands = ["Tesla"] })], p, Ctx());
        Assert.False(d.HasAd);
        Assert.StartsWith("Platform brand safety", d.NoAdReason);
    }

    [Fact]
    public void Inactive_out_of_window_or_wrong_platform_campaigns_are_rejected()
    {
        var t = new Targeting { Brands = ["Tesla"] };
        var paused = Campaign("paused", t) with { Status = CampaignStatus.Paused };
        var future = Campaign("future", t) with { StartAt = Noon.AddDays(1) };
        var ended = Campaign("ended", t) with { EndAt = Noon.AddDays(-1) };
        var other = Campaign("other", t with { Platforms = ["extension"] });
        var geo = Campaign("geo", t with { Geo = ["CZ"] });

        var d = Matcher.Decide("v1", TeslaOnRoad, [paused, future, ended, other, geo], Platform(), Ctx());

        Assert.False(d.HasAd);
        Assert.All(d.Candidates, c => Assert.NotNull(c.RejectedBecause));
    }

    [Fact]
    public void Bid_in_model_not_enabled_on_platform_is_ignored()
    {
        var cpcOnly = Campaign("cpc", new Targeting { Brands = ["Tesla"] }, bids: [new Bid(PricingModel.Cpc, 0.5m)]);

        var cpmPlatform = Matcher.Decide("v1", TeslaOnRoad, [cpcOnly], Platform(PricingModel.Cpm), Ctx());
        Assert.Contains("pricing model", cpmPlatform.Candidates[0].RejectedBecause);

        var both = Matcher.Decide("v1", TeslaOnRoad, [cpcOnly], Platform(PricingModel.Cpm, PricingModel.Cpc), Ctx());
        Assert.Equal(1.0m, both.Winner!.Ecpm); // 0.5 € * 0.002 CTR * 1000
    }

    [Fact]
    public void Cpc_uses_observed_ctr_once_enough_impressions()
    {
        var p = Platform(PricingModel.Cpc).Pricing;
        var state = new CampaignDeliveryState(Impressions: 2000, Clicks: 20); // 1 %
        Assert.Equal(5m, Pricing.Ecpm(new Bid(PricingModel.Cpc, 0.5m), p, state));
    }

    [Fact]
    public void Flat_sponsorship_wins_when_it_matches()
    {
        var sponsor = Campaign("sponsor", new Targeting { Categories = ["automotive"] }, bids: [new Bid(PricingModel.Flat, 5000m)]);
        var cpm = Campaign("cpm", new Targeting { Brands = ["Tesla"] }, cpm: 20m);
        var d = Matcher.Decide("v1", TeslaOnRoad, [sponsor, cpm], Platform(PricingModel.Cpm, PricingModel.Flat), Ctx());
        Assert.Equal("sponsor", d.Winner!.Campaign.Id);
    }

    [Fact]
    public void Creative_must_fit_a_platform_slot_and_exact_size_is_preferred()
    {
        var creatives = new[]
        {
            new Creative("sky", "u", "c", 160, 600),   // fits no slot
            new Creative("small", "u", "c", 300, 50),  // fits, not exact
            new Creative("mrec", "u", "c", 300, 250),  // exact for "right"
        };
        var c = Campaign("c", new Targeting { Brands = ["Tesla"] }, creatives: creatives);
        var d = Matcher.Decide("v1", TeslaOnRoad, [c], Platform(), Ctx());
        Assert.Equal("mrec", d.Winner!.Creative!.Id);
        Assert.Equal(SlotPosition.Right, d.Winner.Slot!.Position);

        var noFit = Campaign("n", new Targeting { Brands = ["Tesla"] }, creatives: [creatives[0]]);
        Assert.Contains("banner slots", Matcher.Decide("v1", TeslaOnRoad, [noFit], Platform(), Ctx()).Candidates[0].RejectedBecause);
    }

    [Theory]
    [InlineData(100.0, 0, 0, false)] // last ad 20 s ago (< 60 s)
    [InlineData(50.0, 0, 0, true)]   // 70 s ago
    [InlineData(null, 3, 0, false)]  // max 3 per video
    [InlineData(null, 0, 5, false)]  // max 5 per viewer per day
    public void Platform_frequency_rules(double? lastAdAt, int inVideo, int today, bool expectAd)
    {
        var p = Platform() with { Frequency = new() { MinIntervalSec = 60, MaxPerVideo = 3, MaxPerViewerPerDay = 5 } };
        var ctx = Ctx(c => c with { LastAdAtSec = lastAdAt, AdsShownInVideo = inVideo, AdsShownToViewerToday = today });
        var d = Matcher.Decide("v1", TeslaOnRoad, [Campaign("t", new Targeting { Brands = ["Tesla"] })], p, ctx);
        Assert.Equal(expectAd, d.HasAd);
    }

    [Fact]
    public void Min_interval_is_measured_from_playback_time_not_segment_bounds()
    {
        var p = Platform() with { Frequency = new() { MinIntervalSec = 60 } };
        var longSeg = Segment(entities: [new ContentEntity("brand", "Tesla", 0.9)], start: 0, end: 300);
        var c = new[] { Campaign("t", new Targeting { Brands = ["Tesla"] }) };
        Assert.False(Matcher.Decide("v", longSeg, c, p, Ctx(x => x with { LastAdAtSec = 10, PlaybackTimeSec = 40 })).HasAd);
        Assert.True(Matcher.Decide("v", longSeg, c, p, Ctx(x => x with { LastAdAtSec = 10, PlaybackTimeSec = 80 })).HasAd);
    }

    [Fact]
    public void Campaign_caps_and_budgets_are_respected()
    {
        var t = new Targeting { Brands = ["Tesla"] };
        var capped = Campaign("capped", t) with { FrequencyCap = new(PerViewerPerVideo: 1) };
        var broke = Campaign("broke", t) with { Budget = new(Total: 100m) };
        var ctx = Ctx(c => c with
        {
            Campaigns = new Dictionary<string, CampaignDeliveryState>
            {
                ["capped"] = new(ViewerImpressionsInVideo: 1),
                ["broke"] = new(SpentTotal: 100m),
            },
        });
        var d = Matcher.Decide("v1", TeslaOnRoad, [capped, broke], Platform(), ctx);
        Assert.False(d.HasAd);
    }

    [Fact]
    public void Overspending_campaign_is_paced_down()
    {
        var c = Campaign("c", new Targeting { Brands = ["Tesla"] }) with { Budget = new(Daily: 100m) };
        // at noon (50 % of day) already 80 % of daily budget spent -> factor 0.5/0.8
        var factor = AdMatcher.PacingFactor(c, new CampaignDeliveryState(SpentToday: 80m), Noon);
        Assert.Equal(0.625, factor, 3);
        Assert.Equal(1.0, AdMatcher.PacingFactor(c, new CampaignDeliveryState(SpentToday: 30m), Noon));
    }

    [Fact]
    public void Competitor_in_same_exclusivity_group_is_not_shown_back_to_back()
    {
        var bmw = Campaign("bmw", new Targeting { Categories = ["automotive"] }) with { ExclusivityKey = "automotive" };
        var ctx = Ctx(c => c with { PreviousCampaignId = "audi", PreviousExclusivityKey = "automotive" });
        Assert.False(Matcher.Decide("v1", TeslaOnRoad, [bmw], Platform(), ctx).HasAd);

        var sameAdvertiser = Ctx(c => c with { PreviousCampaignId = "bmw", PreviousExclusivityKey = "automotive" });
        Assert.True(Matcher.Decide("v1", TeslaOnRoad, [bmw], Platform(), sameAdvertiser).HasAd);
    }

    [Fact]
    public void Missing_segment_returns_reason()
    {
        var d = Matcher.Decide("v1", null, [], Platform(), Ctx());
        Assert.False(d.HasAd);
        Assert.NotNull(d.NoAdReason);
    }
}
