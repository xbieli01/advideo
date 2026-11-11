using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Infrastructure.AdServing;

namespace Advideo.Orchestrator.Tests;

/// <summary>Behaviour every ledger implementation must satisfy (in-memory always, Redis when available).</summary>
public abstract class DeliveryLedgerContract
{
    protected static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh, isolated ledger (unique key prefix for shared backends).</summary>
    protected abstract IDeliveryLedger CreateLedger();

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private string C(string id) => $"{id}-{_run}";

    private IssuedDecision D(string id, PricingModel m, decimal amount, string campaign = "c1", string? viewer = "v1", string video = "vid") =>
        new($"{id}-{_run}", "p", video, C(campaign), viewer is null ? null : viewer + _run, m, amount, 30, Now);

    private async Task<CampaignDeliveryState> State(IDeliveryLedger l, string campaign, string? viewer, DateTimeOffset at) =>
        (await l.CampaignStatesAsync([C(campaign)], viewer is null ? null : viewer + _run, "vid", at))[C(campaign)];

    [SkippableFact]
    public async Task Cpm_charges_per_impression_and_counts_viewer_frequency()
    {
        var l = CreateLedger();
        var o = await l.RecordAsync(D("d1", PricingModel.Cpm, 8m), AdEventType.Impression, 31, Now);
        Assert.Equal(0.008m, o.Cost);
        Assert.Equal(0m, (await l.RecordAsync(D("d1", PricingModel.Cpm, 8m), AdEventType.Click, null, Now)).Cost);

        var s = await State(l, "c1", "v1", Now);
        Assert.Equal(0.008m, s.SpentTotal);
        Assert.Equal(0.008m, s.SpentToday);
        Assert.Equal(1, s.ViewerImpressionsInVideo);
        Assert.Equal(1, s.ViewerImpressionsToday);
        Assert.Equal((1L, 1L), (s.Impressions, s.Clicks));

        var v = await l.ViewerAsync("v1" + _run, "vid", Now);
        Assert.Equal((1, 1, 31d, C("c1")), (v.AdsInVideo, v.AdsToday, v.LastAdAtSec!.Value, v.LastCampaignId));
    }

    [SkippableFact]
    public async Task Cpc_charges_on_click_cpv_on_viewable_flat_never()
    {
        var l = CreateLedger();
        Assert.Equal(0m, (await l.RecordAsync(D("a", PricingModel.Cpc, 1.5m), AdEventType.Impression, null, Now)).Cost);
        Assert.Equal(1.5m, (await l.RecordAsync(D("a", PricingModel.Cpc, 1.5m), AdEventType.Click, null, Now)).Cost);
        Assert.Equal(0.02m, (await l.RecordAsync(D("b", PricingModel.Cpv, 0.02m), AdEventType.Viewable, null, Now)).Cost);
        Assert.Equal(0m, (await l.RecordAsync(D("c", PricingModel.Flat, 5000m), AdEventType.Impression, null, Now)).Cost);
    }

    [SkippableFact]
    public async Task Duplicate_events_are_ignored()
    {
        var l = CreateLedger();
        await l.RecordAsync(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        var dup = await l.RecordAsync(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        Assert.True(dup.Duplicate);
        var stats = await l.StatsAsync(C("c1"), Now);
        Assert.Equal(1, stats.Impressions);
        Assert.Equal(0.01m, stats.SpentTotal);
    }

    [SkippableFact]
    public async Task Daily_counters_roll_over_at_utc_midnight()
    {
        var l = CreateLedger();
        await l.RecordAsync(D("d", PricingModel.Cpm, 10m), AdEventType.Impression, null, Now);
        var tomorrow = Now.AddDays(1);
        var s = await State(l, "c1", "v1", tomorrow);
        Assert.Equal(0m, s.SpentToday);
        Assert.Equal(0.01m, s.SpentTotal);
        Assert.Equal(0, s.ViewerImpressionsToday);
        Assert.Equal(1, s.ViewerImpressionsInVideo);
        Assert.Equal(0, (await l.ViewerAsync("v1" + _run, "vid", tomorrow)).AdsToday);
    }

    [SkippableFact]
    public async Task Anonymous_viewer_counts_campaign_stats_only_and_unknown_campaign_is_empty()
    {
        var l = CreateLedger();
        await l.RecordAsync(D("d", PricingModel.Cpm, 10m, viewer: null), AdEventType.Impression, null, Now);
        Assert.Equal(1, (await l.StatsAsync(C("c1"), Now)).Impressions);
        Assert.Equal(ViewerState.Empty, await l.ViewerAsync(null, "vid", Now));
        Assert.Equal(0, (await State(l, "never", "v1", Now)).Impressions);
    }

    [SkippableFact]
    public async Task Stats_report_ctr()
    {
        var l = CreateLedger();
        for (var i = 0; i < 4; i++) await l.RecordAsync(D($"i{i}", PricingModel.Cpm, 1m), AdEventType.Impression, null, Now);
        await l.RecordAsync(D("i0", PricingModel.Cpm, 1m), AdEventType.Click, null, Now);
        var s = await l.StatsAsync(C("c1"), Now);
        Assert.Equal(0.25, s.Ctr);
        Assert.Equal(0.004m, s.SpentToday);
    }
}

public sealed class InMemoryDeliveryLedgerTests : DeliveryLedgerContract
{
    protected override IDeliveryLedger CreateLedger() => new InMemoryDeliveryLedger();

    [SkippableFact]
    public async Task Decision_log_expires_entries()
    {
        var log = new InMemoryDecisionLog(TimeSpan.FromMinutes(5));
        var d = new IssuedDecision("x", "p", "v", "c", null, PricingModel.Cpm, 1m, 0, DateTimeOffset.UtcNow.AddMinutes(-10));
        await log.RecordAsync(d);
        Assert.Null(await log.GetAsync("x"));
        await log.RecordAsync(d with { DecisionId = "y", IssuedAt = DateTimeOffset.UtcNow });
        Assert.NotNull(await log.GetAsync("y"));
    }
}

/// <summary>
/// Runs the ledger contract over the Redis protocol: against <c>ADVIDEO_TEST_REDIS</c> if set,
/// otherwise against an in-process Microsoft Garnet server (Redis-compatible), so CI needs no Redis.
/// </summary>
public sealed class RedisDeliveryLedgerTests : DeliveryLedgerContract
{
    private static readonly Lazy<StackExchange.Redis.IConnectionMultiplexer> Conn = new(() =>
    {
        var cs = Environment.GetEnvironmentVariable("ADVIDEO_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(cs))
        {
            var port = FreePort();
            var server = new Garnet.GarnetServer(["--bind", "127.0.0.1", "--port", port.ToString()]);
            server.Start();
            Embedded = server; // keep alive for the test run
            cs = $"127.0.0.1:{port}";
        }
        return StackExchange.Redis.ConnectionMultiplexer.Connect(cs);
    });

    private static Garnet.GarnetServer? Embedded;

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    protected override IDeliveryLedger CreateLedger() =>
        new Advideo.Orchestrator.Infrastructure.Redis.RedisDeliveryLedger(Conn.Value,
            new Advideo.Orchestrator.Infrastructure.Redis.RedisDeliveryOptions { KeyPrefix = $"advideo-test-{Guid.NewGuid():N}:" });

    [Fact]
    public async Task Decision_log_round_trip_with_ttl()
    {
        var log = new Advideo.Orchestrator.Infrastructure.Redis.RedisDecisionLog(Conn.Value,
            new Advideo.Orchestrator.Infrastructure.Redis.RedisDeliveryOptions { KeyPrefix = $"advideo-test-{Guid.NewGuid():N}:", DecisionTtl = TimeSpan.FromMinutes(1) });
        var d = new IssuedDecision("dcs_x", "p", "v", "c", "h", PricingModel.Cpv, 0.02m, 12.5, Now);
        await log.RecordAsync(d);
        Assert.Equal(d, await log.GetAsync("dcs_x"));
        Assert.Null(await log.GetAsync("dcs_missing"));
    }
}
