using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Infrastructure.AdServing;

namespace Advideo.Orchestrator.Tests;

public class DeliveryLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static IssuedDecision D(string id, PricingModel m, decimal amount, string campaign = "c1", string? viewer = "v1", string video = "vid") =>
        new(id, "p", video, campaign, viewer, m, amount, 30, Now);

    [Fact]
    public void Cpm_charges_per_impression_and_counts_viewer_frequency()
    {
        var l = new InMemoryDeliveryLedger();
        var o = l.Record(D("d1", PricingModel.Cpm, 8m), AdEventType.Impression, 31, Now);
        Assert.Equal(0.008m, o.Cost);
        Assert.Equal(0m, l.Record(D("d1", PricingModel.Cpm, 8m), AdEventType.Click, null, Now).Cost);

        var s = l.CampaignState("c1", "v1", "vid", Now);
        Assert.Equal(0.008m, s.SpentTotal);
        Assert.Equal(0.008m, s.SpentToday);
        Assert.Equal(1, s.ViewerImpressionsInVideo);
        Assert.Equal(1, s.ViewerImpressionsToday);
        Assert.Equal((1L, 1L), (s.Impressions, s.Clicks));

        var v = l.Viewer("v1", "vid", Now);
        Assert.Equal((1, 1, 31d, "c1"), (v.AdsInVideo, v.AdsToday, v.LastAdAtSec!.Value, v.LastCampaignId));
    }

    [Fact]
    public void Cpc_charges_on_click_and_cpv_on_viewable_flat_never()
    {
        var l = new InMemoryDeliveryLedger();
        Assert.Equal(0m, l.Record(D("a", PricingModel.Cpc, 1.5m), AdEventType.Impression, null, Now).Cost);
        Assert.Equal(1.5m, l.Record(D("a", PricingModel.Cpc, 1.5m), AdEventType.Click, null, Now).Cost);
        Assert.Equal(0.02m, l.Record(D("b", PricingModel.Cpv, 0.02m), AdEventType.Viewable, null, Now).Cost);
        Assert.Equal(0m, l.Record(D("c", PricingModel.Flat, 5000m), AdEventType.Impression, null, Now).Cost);
    }

    [Fact]
    public void Duplicate_events_are_ignored()
    {
        var l = new InMemoryDeliveryLedger();
        l.Record(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        var dup = l.Record(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        Assert.True(dup.Duplicate);
        Assert.Equal(1, l.Stats("c1", Now).Impressions);
        Assert.Equal(0.01m, l.Stats("c1", Now).SpentTotal);
    }

    [Fact]
    public void Daily_counters_roll_over_at_utc_midnight()
    {
        var l = new InMemoryDeliveryLedger();
        l.Record(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        var tomorrow = Now.AddDays(1);
        var s = l.CampaignState("c1", "v1", "vid", tomorrow);
        Assert.Equal(0m, s.SpentToday);
        Assert.Equal(0.01m, s.SpentTotal);
        Assert.Equal(0, s.ViewerImpressionsToday);
        Assert.Equal(1, s.ViewerImpressionsInVideo);
        Assert.Equal(0, l.Viewer("v1", "vid", tomorrow).AdsToday);
    }

    [Fact]
    public void Anonymous_viewer_counts_campaign_stats_only()
    {
        var l = new InMemoryDeliveryLedger();
        l.Record(D("d", PricingModel.Cpm, 10m, viewer: null), AdEventType.Impression, null, Now);
        Assert.Equal(1, l.Stats("c1", Now).Impressions);
        Assert.Equal(ViewerState.Empty, l.Viewer(null, "vid", Now));
    }

    [Fact]
    public void Decision_log_expires_entries()
    {
        var log = new InMemoryDecisionLog(TimeSpan.FromMinutes(5));
        var d = new IssuedDecision("x", "p", "v", "c", null, PricingModel.Cpm, 1m, 0, DateTimeOffset.UtcNow.AddMinutes(-10));
        log.Record(d);
        Assert.Null(log.Get("x"));
        log.Record(d with { DecisionId = "y", IssuedAt = DateTimeOffset.UtcNow });
        Assert.NotNull(log.Get("y"));
    }
}
