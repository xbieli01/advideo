using System.Collections.Concurrent;
using Advideo.AdServing.Matching;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;

namespace Advideo.Orchestrator.Infrastructure.AdServing;

/// <summary>In-memory decision log with TTL (dev / single instance). Production: Redis with expiry.</summary>
public sealed class InMemoryDecisionLog(TimeSpan? ttl = null, TimeProvider? time = null) : IDecisionLog
{
    private readonly ConcurrentDictionary<string, IssuedDecision> _items = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _writes;

    public void Record(IssuedDecision decision)
    {
        _items[decision.DecisionId] = decision;
        if (Interlocked.Increment(ref _writes) % 1000 == 0) Purge();
    }

    public IssuedDecision? Get(string decisionId) =>
        _items.TryGetValue(decisionId, out var d) && _time.GetUtcNow() - d.IssuedAt <= _ttl ? d : null;

    private void Purge()
    {
        var cutoff = _time.GetUtcNow() - _ttl;
        foreach (var (id, d) in _items)
            if (d.IssuedAt < cutoff) _items.TryRemove(id, out _);
    }
}

/// <summary>
/// In-memory delivery ledger (dev / single instance). Same contract later backed by Redis counters
/// (hot path) + Postgres (billing). Days are UTC days.
/// </summary>
public sealed class InMemoryDeliveryLedger : IDeliveryLedger
{
    private sealed class CampaignTotals
    {
        public long Impressions, Viewables, Clicks, Closes;
        public decimal SpentTotal;
        public readonly Dictionary<DateOnly, decimal> SpentByDay = [];
    }

    private sealed class ViewerVideo
    {
        public int Ads;
        public double? LastAdAtSec;
        public string? LastCampaignId;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, CampaignTotals> _campaigns = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Viewer, string Video), ViewerVideo> _viewerVideo = [];
    private readonly Dictionary<(string Viewer, DateOnly Day), int> _viewerDay = [];
    private readonly Dictionary<(string Viewer, string Campaign, string Video), int> _viewerCampaignVideo = [];
    private readonly Dictionary<(string Viewer, string Campaign, DateOnly Day), int> _viewerCampaignDay = [];
    private readonly HashSet<(string DecisionId, AdEventType Type)> _seen = [];

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);

    public EventOutcome Record(IssuedDecision d, AdEventType type, double? atSec, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_seen.Add((d.DecisionId, type))) return new EventOutcome(false, true, 0m);

            var totals = Totals(d.CampaignId);
            var cost = Pricing.CostOf(d.Model, d.BidAmount, type);
            totals.SpentTotal += cost;
            var day = Day(now);
            totals.SpentByDay[day] = totals.SpentByDay.GetValueOrDefault(day) + cost;

            switch (type)
            {
                case AdEventType.Impression:
                    totals.Impressions++;
                    if (d.ViewerHash is { } v)
                    {
                        var vv = _viewerVideo.TryGetValue((v, d.VideoId), out var x) ? x : _viewerVideo[(v, d.VideoId)] = new ViewerVideo();
                        vv.Ads++;
                        vv.LastAdAtSec = atSec ?? d.ShowFromSec;
                        vv.LastCampaignId = d.CampaignId;
                        Inc(_viewerDay, (v, day));
                        Inc(_viewerCampaignVideo, (v, d.CampaignId, d.VideoId));
                        Inc(_viewerCampaignDay, (v, d.CampaignId, day));
                    }
                    break;
                case AdEventType.Viewable: totals.Viewables++; break;
                case AdEventType.Click: totals.Clicks++; break;
                case AdEventType.Close: totals.Closes++; break;
            }
            return new EventOutcome(true, false, cost);
        }
    }

    public CampaignDeliveryState CampaignState(string campaignId, string? viewerHash, string videoId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_campaigns.TryGetValue(campaignId, out var t)) t = new CampaignTotals();
            var day = Day(now);
            int inVideo = 0, today = 0;
            if (viewerHash is { } v)
            {
                inVideo = _viewerCampaignVideo.GetValueOrDefault((v, campaignId, videoId));
                today = _viewerCampaignDay.GetValueOrDefault((v, campaignId, day));
            }
            return new CampaignDeliveryState(t.SpentTotal, t.SpentByDay.GetValueOrDefault(day), inVideo, today, t.Impressions, t.Clicks);
        }
    }

    public ViewerState Viewer(string? viewerHash, string videoId, DateTimeOffset now)
    {
        if (viewerHash is null) return ViewerState.Empty;
        lock (_lock)
        {
            var today = _viewerDay.GetValueOrDefault((viewerHash, Day(now)));
            return _viewerVideo.TryGetValue((viewerHash, videoId), out var vv)
                ? new ViewerState(vv.Ads, today, vv.LastAdAtSec, vv.LastCampaignId)
                : ViewerState.Empty with { AdsToday = today };
        }
    }

    public CampaignStats Stats(string campaignId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_campaigns.TryGetValue(campaignId, out var t)) t = new CampaignTotals();
            var ctr = t.Impressions == 0 ? 0 : (double)t.Clicks / t.Impressions;
            return new CampaignStats(campaignId, t.Impressions, t.Viewables, t.Clicks, t.Closes, Math.Round(ctr, 6),
                t.SpentTotal, t.SpentByDay.GetValueOrDefault(Day(now)));
        }
    }

    private CampaignTotals Totals(string id) =>
        _campaigns.TryGetValue(id, out var t) ? t : _campaigns[id] = new CampaignTotals();

    private static void Inc<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull =>
        d[k] = d.GetValueOrDefault(k) + 1;
}
