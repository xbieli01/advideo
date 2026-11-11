using System.Globalization;
using System.Text.Json;
using Advideo.AdServing.Matching;
using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Infrastructure.Postgres;
using StackExchange.Redis;

namespace Advideo.Orchestrator.Infrastructure.Redis;

public sealed class RedisDeliveryOptions
{
    public string KeyPrefix { get; set; } = "advideo:";
    public TimeSpan DecisionTtl { get; set; } = TimeSpan.FromHours(1);
    /// <summary>How long per-viewer/video counters live (frequency caps per video).</summary>
    public TimeSpan ViewerVideoTtl { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Decision log in Redis with expiry (shared across API instances).</summary>
public sealed class RedisDecisionLog(IConnectionMultiplexer redis, RedisDeliveryOptions o) : IDecisionLog
{
    private IDatabase Db => redis.GetDatabase();
    private string Key(string id) => $"{o.KeyPrefix}dec:{id}";

    public Task RecordAsync(IssuedDecision d, CancellationToken ct = default) =>
        Db.StringSetAsync(Key(d.DecisionId), JsonSerializer.Serialize(d, PostgresDocumentStore<IssuedDecision>.Json), o.DecisionTtl);

    public async Task<IssuedDecision?> GetAsync(string id, CancellationToken ct = default)
    {
        var v = await Db.StringGetAsync(Key(id));
        return v.IsNullOrEmpty ? null : JsonSerializer.Deserialize<IssuedDecision>(v.ToString(), PostgresDocumentStore<IssuedDecision>.Json);
    }
}

/// <summary>
/// Delivery counters in Redis (shared, atomic increments). Money is kept as integer micro-units to avoid
/// float drift (1 EUR = 1 000 000). Event dedupe via SET NX. Daily keys expire after 3 days.
/// </summary>
public sealed class RedisDeliveryLedger(IConnectionMultiplexer redis, RedisDeliveryOptions o) : IDeliveryLedger
{
    private const decimal Micro = 1_000_000m;
    private static readonly TimeSpan DayTtl = TimeSpan.FromDays(3);
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromDays(7);

    private IDatabase Db => redis.GetDatabase();
    private static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private string KCampaign(string c) => $"{o.KeyPrefix}c:{{{c}}}";
    private string KCampaignDay(string c, string day) => $"{o.KeyPrefix}c:{{{c}}}:spent:{day}";
    private string KViewerVideo(string v, string video) => $"{o.KeyPrefix}v:{{{v}}}:vid:{video}";
    private string KViewerDay(string v, string day) => $"{o.KeyPrefix}v:{{{v}}}:d:{day}";
    private string KViewerCampaignVideo(string v, string c, string video) => $"{o.KeyPrefix}v:{{{v}}}:c:{c}:vid:{video}";
    private string KViewerCampaignDay(string v, string c, string day) => $"{o.KeyPrefix}v:{{{v}}}:c:{c}:d:{day}";
    private string KSeen(string decisionId, AdEventType t) => $"{o.KeyPrefix}seen:{decisionId}:{t}";

    public async Task<EventOutcome> RecordAsync(IssuedDecision d, AdEventType type, double? atSec, DateTimeOffset now, CancellationToken ct = default)
    {
        var db = Db;
        if (!await db.StringSetAsync(KSeen(d.DecisionId, type), 1, DedupeTtl, When.NotExists))
            return new EventOutcome(false, true, 0m);

        var cost = Pricing.CostOf(d.Model, d.BidAmount, type);
        var micros = (long)decimal.Round(cost * Micro);
        var day = Day(now);
        var b = db.CreateBatch();
        var tasks = new List<Task>();
        var ck = KCampaign(d.CampaignId);
        if (micros != 0)
        {
            tasks.Add(b.HashIncrementAsync(ck, "spent", micros));
            tasks.Add(b.StringIncrementAsync(KCampaignDay(d.CampaignId, day), micros));
            tasks.Add(b.KeyExpireAsync(KCampaignDay(d.CampaignId, day), DayTtl));
        }
        tasks.Add(b.HashIncrementAsync(ck, type switch
        {
            AdEventType.Impression => "impressions",
            AdEventType.Viewable => "viewables",
            AdEventType.Click => "clicks",
            _ => "closes",
        }));

        if (type == AdEventType.Impression && d.ViewerHash is { } v)
        {
            var vv = KViewerVideo(v, d.VideoId);
            tasks.Add(b.HashIncrementAsync(vv, "ads"));
            tasks.Add(b.HashSetAsync(vv, [
                new HashEntry("lastAdAt", (atSec ?? d.ShowFromSec).ToString("R", CultureInfo.InvariantCulture)),
                new HashEntry("lastCampaign", d.CampaignId),
            ]));
            tasks.Add(b.KeyExpireAsync(vv, o.ViewerVideoTtl));
            foreach (var (key, ttl) in new[]
                     {
                         (KViewerDay(v, day), DayTtl),
                         (KViewerCampaignVideo(v, d.CampaignId, d.VideoId), o.ViewerVideoTtl),
                         (KViewerCampaignDay(v, d.CampaignId, day), DayTtl),
                     })
            {
                tasks.Add(b.StringIncrementAsync(key));
                tasks.Add(b.KeyExpireAsync(key, ttl));
            }
        }
        b.Execute();
        await Task.WhenAll(tasks);
        return new EventOutcome(true, false, cost);
    }

    public async Task<IReadOnlyDictionary<string, CampaignDeliveryState>> CampaignStatesAsync(
        IReadOnlyCollection<string> campaignIds, string? viewerHash, string videoId, DateTimeOffset now, CancellationToken ct = default)
    {
        var ids = campaignIds.Distinct().ToList();
        var day = Day(now);
        var b = Db.CreateBatch();
        var reads = ids.Select(id => (
            id,
            totals: b.HashGetAsync(KCampaign(id), ["spent", "impressions", "clicks"]),
            today: b.StringGetAsync(KCampaignDay(id, day)),
            inVideo: viewerHash is null ? Task.FromResult(RedisValue.Null) : b.StringGetAsync(KViewerCampaignVideo(viewerHash, id, videoId)),
            vToday: viewerHash is null ? Task.FromResult(RedisValue.Null) : b.StringGetAsync(KViewerCampaignDay(viewerHash, id, day))
        )).ToList();
        b.Execute();

        var result = new Dictionary<string, CampaignDeliveryState>(StringComparer.Ordinal);
        foreach (var r in reads)
        {
            var t = await r.totals;
            result[r.id] = new CampaignDeliveryState(
                SpentTotal: L(t[0]) / Micro,
                SpentToday: L(await r.today) / Micro,
                ViewerImpressionsInVideo: (int)L(await r.inVideo),
                ViewerImpressionsToday: (int)L(await r.vToday),
                Impressions: L(t[1]),
                Clicks: L(t[2]));
        }
        return result;
    }

    public async Task<ViewerState> ViewerAsync(string? viewerHash, string videoId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (viewerHash is null) return ViewerState.Empty;
        var db = Db;
        var vv = db.HashGetAsync(KViewerVideo(viewerHash, videoId), ["ads", "lastAdAt", "lastCampaign"]);
        var today = db.StringGetAsync(KViewerDay(viewerHash, Day(now)));
        var h = await vv;
        double? last = h[1].IsNull ? null : double.Parse(h[1].ToString(), CultureInfo.InvariantCulture);
        return new ViewerState((int)L(h[0]), (int)L(await today), last, h[2].IsNull ? null : h[2].ToString());
    }

    public async Task<CampaignStats> StatsAsync(string campaignId, DateTimeOffset now, CancellationToken ct = default)
    {
        var db = Db;
        var t = await db.HashGetAsync(KCampaign(campaignId), ["impressions", "viewables", "clicks", "closes", "spent"]);
        var today = L(await db.StringGetAsync(KCampaignDay(campaignId, Day(now))));
        long imp = L(t[0]), clicks = L(t[2]);
        return new CampaignStats(campaignId, imp, L(t[1]), clicks, L(t[3]),
            imp == 0 ? 0 : Math.Round((double)clicks / imp, 6), L(t[4]) / Micro, today / Micro);
    }

    private static long L(RedisValue v) => v.IsNullOrEmpty ? 0 : (long)v;
}

public sealed class RedisReadinessProbe(IConnectionMultiplexer redis) : IReadinessProbe
{
    public string Name => "redis";
    public Task CheckAsync(CancellationToken ct) => redis.GetDatabase().PingAsync();
}
