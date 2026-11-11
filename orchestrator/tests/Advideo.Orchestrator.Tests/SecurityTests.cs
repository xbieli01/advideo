using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Advideo.Orchestrator.Tests;

public class SecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityTests(WebApplicationFactory<Program> factory)
    {
        var seed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../seed/adserving.sample.json"));
        _factory = factory.WithWebHostBuilder(b => b.WithTestKeys()
            .UseSetting("AdServing:SeedFile", seed)
            .UseSetting("RateLimit:AdsPerMinutePerClient", "5")
            .UseSetting("Storage:FileSystem:RootPath", Path.Combine(Path.GetTempPath(), "advideo-sec-" + Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData("GET", "/campaigns")]
    [InlineData("GET", "/platforms")]
    [InlineData("GET", "/videos")]
    [InlineData("GET", "/ads/explain?platform=demo-portal&videoId=x&t=1")]
    [InlineData("POST", "/campaigns/tesla-model-y/pause")]
    public async Task Management_endpoints_require_a_key(string method, string url)
    {
        var r = await _factory.ClientWithKey(null).SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("X-Api-Key", r.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Wrong_key_is_rejected()
    {
        var r = await _factory.ClientWithKey("nope").GetAsync("/campaigns");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Reporting_key_reads_but_cannot_write()
    {
        var c = _factory.ClientWithKey(TestKeys.Reporting);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/campaigns/tesla-model-y/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/campaigns/tesla-model-y/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync("/platforms/demo-portal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/ads/explain?platform=demo-portal&videoId=x&t=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/videos")).StatusCode);
    }

    [Fact]
    public async Task Snippet_endpoints_are_public_and_rate_limited()
    {
        var c = _factory.ClientWithKey(null);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
            codes.Add((await c.GetAsync("/ads/decision?platform=demo-portal&videoId=missing&t=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, codes[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Health_and_readiness_are_public()
    {
        var c = _factory.ClientWithKey(null);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
        var ready = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/readyz");
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal("ok", ready.GetProperty("checks").GetProperty("mapStore").GetString());
    }
}
