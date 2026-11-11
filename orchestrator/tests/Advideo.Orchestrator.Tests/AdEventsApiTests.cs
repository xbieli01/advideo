using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.Orchestrator.Core.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Advideo.Orchestrator.Tests;

/// <summary>T-005c: events feed server-side counters which then drive frequency, caps and budgets.</summary>
public class AdEventsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string MapJson = """
    {"schemaVersion":"1.0","videoId":"evt-vid","durationSec":600,"generatedAt":"2026-11-11T10:00:00Z",
     "segments":[
       {"startSec":0,"endSec":300,"keywords":[{"term":"car","score":0.8}],
        "entities":[{"type":"brand","label":"Tesla","score":0.9}],"categories":["automotive"]},
       {"startSec":300,"endSec":600,"keywords":[{"term":"car","score":0.8}],
        "entities":[{"type":"brand","label":"Tesla","score":0.9}],"categories":["automotive"]}
     ]}
    """;

    private readonly WebApplicationFactory<Program> _factory;

    public AdEventsApiTests(WebApplicationFactory<Program> factory)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "advideo-evt-" + Guid.NewGuid().ToString("N"));
        var seed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../seed/adserving.sample.json"));
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage:FileSystem:RootPath", dataDir);
            b.UseSetting("AdServing:SeedFile", seed);
        });
        _factory.Services.GetRequiredService<IMapStore>()
            .SaveAsync("evt-vid", new MemoryStream(Encoding.UTF8.GetBytes(MapJson))).GetAwaiter().GetResult();
    }

    private static async Task<JsonElement?> Decide(HttpClient c, string qs)
    {
        var r = await c.GetAsync("/ads/decision?" + qs);
        return r.StatusCode == HttpStatusCode.OK ? await r.Content.ReadFromJsonAsync<JsonElement>(Json) : null;
    }

    private static Task<HttpResponseMessage> Event(HttpClient c, string decisionId, string type, double? t = null) =>
        c.PostAsJsonAsync("/ads/events", new { decisionId, type, t }, Json);

    [Fact]
    public async Task Impression_and_click_update_stats_and_spend()
    {
        var c = _factory.CreateClient();
        var d = (await Decide(c, "platform=demo-portal&videoId=evt-vid&t=10&viewer=u-stats"))!.Value;
        var id = d.GetProperty("decisionId").GetString()!;
        Assert.Equal("/ads/events", d.GetProperty("tracking").GetProperty("eventsUrl").GetString());

        var imp = await Event(c, id, "impression", 10);
        Assert.Equal(HttpStatusCode.Accepted, imp.StatusCode);
        Assert.Equal(0.008m, (await imp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("cost").GetDecimal());
        await Event(c, id, "click");
        var dup = await (await Event(c, id, "impression")).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(dup.GetProperty("duplicate").GetBoolean());

        var stats = await c.GetFromJsonAsync<JsonElement>("/campaigns/tesla-model-y/stats", Json);
        Assert.True(stats.GetProperty("impressions").GetInt64() >= 1);
        Assert.True(stats.GetProperty("clicks").GetInt64() >= 1);
        Assert.True(stats.GetProperty("spentTotal").GetDecimal() >= 0.008m);
    }

    [Fact]
    public async Task Unknown_decision_is_404()
    {
        var r = await Event(_factory.CreateClient(), "dcs_nope", "impression");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Server_side_min_interval_blocks_next_ad_for_same_viewer_only()
    {
        var c = _factory.CreateClient();
        var d = (await Decide(c, "platform=demo-portal&videoId=evt-vid&t=10&viewer=u-int"))!.Value;
        await Event(c, d.GetProperty("decisionId").GetString()!, "impression", 10);

        Assert.Null(await Decide(c, "platform=demo-portal&videoId=evt-vid&t=40&viewer=u-int"));    // 30 s later
        Assert.NotNull(await Decide(c, "platform=demo-portal&videoId=evt-vid&t=40&viewer=u-other"));
        Assert.NotNull(await Decide(c, "platform=demo-portal&videoId=evt-vid&t=80&viewer=u-int"));  // 70 s later
    }

    [Fact]
    public async Task Server_side_max_ads_per_video()
    {
        var c = _factory.CreateClient();
        foreach (var t in new[] { 10, 100, 200 })
        {
            var d = (await Decide(c, $"platform=demo-portal&videoId=evt-vid&t={t}&viewer=u-max"))!.Value;
            await Event(c, d.GetProperty("decisionId").GetString()!, "impression", t);
        }
        var resp = await c.GetAsync("/ads/decision?platform=demo-portal&videoId=evt-vid&t=400&viewer=u-max");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Contains("ads per video", resp.Headers.GetValues("X-Advideo-NoAd-Reason").Single());
    }

    [Fact]
    public async Task Campaign_frequency_cap_and_daily_budget_use_real_counters()
    {
        var c = _factory.CreateClient();
        var created = await c.PostAsJsonAsync("/campaigns", new
        {
            id = "capped-auto", advertiserId = "x", name = "capped", status = "active", priority = 10,
            bids = new[] { new { model = "cpm", amount = 100.0 } },
            budget = new { daily = 0.1 },                  // exactly one impression (100 / 1000)
            frequencyCap = new { perViewerPerVideo = 1 },
            targeting = new { categories = new[] { "automotive" }, platforms = new[] { "demo-portal" } },
            creatives = new[] { new { id = "cap-300", url = "u", clickUrl = "c", width = 300, height = 250 } },
        }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var first = (await Decide(c, "platform=demo-portal&videoId=evt-vid&t=10&viewer=u-cap"))!.Value;
        Assert.Equal("capped-auto", first.GetProperty("ad").GetProperty("campaignId").GetString());
        await Event(c, first.GetProperty("decisionId").GetString()!, "impression", 10);

        // same viewer later: per-viewer cap; another viewer: daily budget already spent
        var explain = await c.GetFromJsonAsync<JsonElement>("/ads/explain?platform=demo-portal&videoId=evt-vid&t=400&viewer=u-cap2", Json);
        var capped = explain.GetProperty("candidates").EnumerateArray().Single(x => x.GetProperty("campaignId").GetString() == "capped-auto");
        Assert.Equal("Daily budget spent.", capped.GetProperty("rejectedBecause").GetString());
        await c.DeleteAsync("/campaigns/capped-auto");
    }
}
