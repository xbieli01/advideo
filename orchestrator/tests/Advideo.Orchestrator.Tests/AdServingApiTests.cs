using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.Orchestrator.Core.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Advideo.Orchestrator.Tests;

public class AdServingApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string MapJson = """
    {"schemaVersion":"1.0","videoId":"vid-1","durationSec":15,"generatedAt":"2026-11-11T10:00:00Z",
     "segments":[
       {"startSec":0,"endSec":5,"keywords":[{"term":"car","score":0.8}],
        "entities":[{"type":"brand","label":"Tesla","score":0.9}],"categories":["automotive"]},
       {"startSec":5,"endSec":10,"keywords":[],"entities":[],"categories":[]},
       {"startSec":10,"endSec":15,"keywords":[{"term":"bottle","score":0.7}],
        "entities":[{"type":"brand","label":"Coca-Cola","score":0.85}],"categories":["beverage"]}
     ]}
    """;

    private readonly WebApplicationFactory<Program> _factory;

    public AdServingApiTests(WebApplicationFactory<Program> factory)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "advideo-ads-" + Guid.NewGuid().ToString("N"));
        var seed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../seed/adserving.sample.json"));
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage:FileSystem:RootPath", dataDir);
            b.UseSetting("AdServing:SeedFile", seed);
        });
        var store = _factory.Services.GetRequiredService<IMapStore>();
        store.SaveAsync("vid-1", new MemoryStream(Encoding.UTF8.GetBytes(MapJson))).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Seeded_platforms_and_campaigns_are_listed()
    {
        var c = _factory.CreateClient();
        var platforms = await c.GetFromJsonAsync<List<JsonElement>>("/platforms", Json);
        Assert.Contains(platforms!, p => p.GetProperty("id").GetString() == "browser-extension");
        Assert.Equal("browserExtension", platforms!.Single(p => p.GetProperty("id").GetString() == "browser-extension").GetProperty("kind").GetString());
        var campaigns = await c.GetFromJsonAsync<List<JsonElement>>("/campaigns", Json);
        Assert.True(campaigns!.Count >= 4);
    }

    [Fact]
    public async Task Decision_returns_brand_matched_ad_with_creative_for_platform_slot()
    {
        var resp = await _factory.CreateClient().GetAsync("/ads/decision?platform=demo-portal&videoId=vid-1&t=2.5&geo=SK");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var d = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var ad = d.GetProperty("ad");
        Assert.Equal("tesla-model-y", ad.GetProperty("campaignId").GetString());
        Assert.Equal("tesla-300x250", ad.GetProperty("creative").GetProperty("id").GetString());
        Assert.Equal("right", ad.GetProperty("slot").GetProperty("position").GetString());
        Assert.Equal("cpm", ad.GetProperty("pricingModel").GetString());
        Assert.Equal(0, ad.GetProperty("showFromSec").GetDouble());
        Assert.Equal(5, ad.GetProperty("showToSec").GetDouble());
        Assert.Equal("brand", d.GetProperty("matchedOn")[0].GetProperty("kind").GetString());
        Assert.StartsWith("dcs_", d.GetProperty("decisionId").GetString());
    }

    [Fact]
    public async Task Segment_without_matching_content_returns_204_with_reason()
    {
        var resp = await _factory.CreateClient().GetAsync("/ads/decision?platform=demo-portal&videoId=vid-1&t=7");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.True(resp.Headers.Contains("X-Advideo-NoAd-Reason"));
    }

    [Fact]
    public async Task Cpc_campaign_only_serves_on_platform_with_cpc_enabled_and_targeted()
    {
        var c = _factory.CreateClient();
        var portal = await c.GetFromJsonAsync<JsonElement>("/ads/decision?platform=demo-portal&videoId=vid-1&t=12", Json);
        Assert.Equal("coca-cola-cpc", portal.GetProperty("ad").GetProperty("campaignId").GetString());
        Assert.Equal("cpc", portal.GetProperty("ad").GetProperty("pricingModel").GetString());

        var ext = await c.GetAsync("/ads/decision?platform=browser-extension&videoId=vid-1&t=12");
        Assert.Equal(HttpStatusCode.NoContent, ext.StatusCode);
    }

    [Fact]
    public async Task Unknown_platform_is_404_and_unknown_video_is_204()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/ads/decision?platform=nope&videoId=vid-1&t=1")).StatusCode);
        var noMap = await c.GetAsync("/ads/decision?platform=demo-portal&videoId=missing&t=1");
        Assert.Equal(HttpStatusCode.NoContent, noMap.StatusCode);
        Assert.Contains("No content map", noMap.Headers.GetValues("X-Advideo-NoAd-Reason").Single());
    }

    [Fact]
    public async Task Viewer_frequency_from_snippet_is_applied()
    {
        var resp = await _factory.CreateClient().GetAsync("/ads/decision?platform=demo-portal&videoId=vid-1&t=2&lastAdAt=0");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Competitor_exclusivity_uses_previous_campaign()
    {
        var d = await _factory.CreateClient().GetFromJsonAsync<JsonElement>(
            "/ads/decision?platform=demo-portal&videoId=vid-1&t=2&prevCampaign=vw-cars", Json);
        Assert.Equal("vw-cars", d.GetProperty("ad").GetProperty("campaignId").GetString());
    }

    [Fact]
    public async Task Explain_lists_all_candidates_with_reasons()
    {
        var e = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/ads/explain?platform=demo-portal&videoId=vid-1&t=2", Json);
        Assert.Equal("tesla-model-y", e.GetProperty("winnerCampaignId").GetString());
        var cands = e.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal("tesla-model-y", cands[0].GetProperty("campaignId").GetString());
        Assert.Contains(cands, x => x.GetProperty("campaignId").GetString() == "nike-running"
                                    && x.GetProperty("rejectedBecause").GetString() == "No targeting match.");
    }

    [Fact]
    public async Task Campaign_crud_validates_and_pause_affects_decisions()
    {
        var c = _factory.CreateClient();
        var bad = await c.PostAsJsonAsync("/campaigns", new
        {
            id = "bad", advertiserId = "x", name = "bad", status = "active",
            bids = new[] { new { model = "cpm", amount = 1.0 } },
            targeting = new { categories = new[] { "automotiv" } },
            creatives = new[] { new { id = "c", url = "u", clickUrl = "c", width = 300, height = 250 } },
        }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("automotiv", await bad.Content.ReadAsStringAsync());

        var ok = await c.PostAsJsonAsync("/campaigns", new
        {
            id = "audi-cars", advertiserId = "audi", name = "Audi", status = "active", priority = 10,
            bids = new[] { new { model = "cpm", amount = 50.0 } },
            targeting = new { categories = new[] { "automotive" } },
            creatives = new[] { new { id = "audi-300", url = "u", clickUrl = "c", width = 300, height = 250 } },
        }, Json);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var won = await c.GetFromJsonAsync<JsonElement>("/ads/decision?platform=demo-portal&videoId=vid-1&t=2", Json);
        Assert.Equal("audi-cars", won.GetProperty("ad").GetProperty("campaignId").GetString());

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/campaigns/audi-cars/pause", null)).StatusCode);
        var after = await c.GetFromJsonAsync<JsonElement>("/ads/decision?platform=demo-portal&videoId=vid-1&t=2", Json);
        Assert.Equal("tesla-model-y", after.GetProperty("ad").GetProperty("campaignId").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/campaigns/audi-cars")).StatusCode);
    }

    [Fact]
    public async Task Platform_validation_rejects_missing_slots()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/platforms", new { id = "p2", name = "P2", slots = Array.Empty<object>() }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
