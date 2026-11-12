using System.Net;
using System.Net.Http.Json;
using Advideo.Orchestrator.Api.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Advideo.Orchestrator.Tests;

public class AdminUiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "advideo-webroot-" + Guid.NewGuid().ToString("N"));

    public AdminUiTests(WebApplicationFactory<Program> factory)
    {
        var admin = Path.Combine(_webRoot, "admin");
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "index.html"), "<!doctype html><title>advideo admin</title>");
        File.WriteAllText(Path.Combine(admin, "main-ABC123.js"), "console.log('admin')");
        _factory = factory.WithWebHostBuilder(b => b.WithTestKeys()
            .UseWebRoot(_webRoot)
            .UseSetting("Storage:FileSystem:RootPath", Path.Combine(Path.GetTempPath(), "advideo-admin-" + Guid.NewGuid().ToString("N"))));
    }

    public void Dispose()
    {
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(TestKeys.Admin, "test-admin", "admin")]
    [InlineData(TestKeys.Reporting, "test-reporting", "reporting")]
    public async Task Auth_me_returns_key_name_and_roles(string key, string name, string role)
    {
        var me = await _factory.ClientWithKey(key).GetFromJsonAsync<AuthMeResponse>("/auth/me");
        Assert.NotNull(me);
        Assert.Equal(name, me!.Name);
        Assert.Equal([role], me.Roles);
    }

    [Fact]
    public async Task Auth_me_requires_a_key()
    {
        var r = await _factory.ClientWithKey(null).GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/admin/campaigns/tesla-model-y")]
    public async Task Spa_routes_serve_index_without_a_key_and_without_cache(string url)
    {
        var r = await _factory.ClientWithKey(null).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("advideo admin", await r.Content.ReadAsStringAsync());
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Hashed_bundles_are_served_as_files_and_cached()
    {
        var r = await _factory.ClientWithKey(null).GetAsync("/admin/main-ABC123.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("console.log('admin')", await r.Content.ReadAsStringAsync());
        Assert.Contains("immutable", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Missing_asset_is_404_not_index()
    {
        var r = await _factory.ClientWithKey(null).GetAsync("/admin/missing-XYZ.js");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Bare_admin_path_redirects_to_trailing_slash()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/admin");
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently, r.StatusCode.ToString());
        Assert.EndsWith("/admin/", r.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Api_data_still_needs_a_key()
    {
        var r = await _factory.ClientWithKey(null).GetAsync("/campaigns");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
