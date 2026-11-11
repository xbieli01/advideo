using System.Net;
using System.Net.Http.Json;
using System.Text;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Advideo.Orchestrator.Tests;

/// <summary>Fake pipeline so the end-to-end flow runs without Python/models.</summary>
file sealed class FakePipelineRunner : IPipelineRunner
{
    public Task<Stream> RunAsync(VideoJob job, CancellationToken ct = default)
    {
        var json = $$"""
        {"schemaVersion":"1.0","videoId":"{{job.VideoId}}","durationSec":12,
         "generatedAt":"2026-07-24T00:00:00Z","segments":[]}
        """;
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }
}

public class OrchestratorApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "advideo-api-" + Guid.NewGuid().ToString("N"));

    public OrchestratorApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.WithTestKeys();
            b.UseSetting("Storage:FileSystem:RootPath", _dataDir);
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IPipelineRunner>();
                services.AddSingleton<IPipelineRunner, FakePipelineRunner>();
            });
        });
    }

    [Fact]
    public async Task Healthz_ok()
    {
        var client = _factory.AdminClient();
        var resp = await client.GetAsync("/healthz");
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Register_processes_and_serves_map()
    {
        var client = _factory.AdminClient();

        var reg = await client.PostAsJsonAsync("/videos", new
        {
            videoId = "demo-1",
            videoUri = "/tmp/demo-1.mp4",
            analyzer = "mock"
        });
        Assert.Equal(HttpStatusCode.Accepted, reg.StatusCode);

        // worker processes asynchronously — poll until Completed
        string? status = null;
        for (var i = 0; i < 50; i++)
        {
            var job = await client.GetFromJsonAsync<JobDto>("/videos/demo-1");
            status = job?.status;
            if (status is "Completed" or "Failed") break;
            await Task.Delay(100);
        }
        Assert.Equal("Completed", status);

        var map = await client.GetAsync("/videos/demo-1/map");
        map.EnsureSuccessStatusCode();
        var body = await map.Content.ReadAsStringAsync();
        Assert.Contains("\"videoId\":\"demo-1\"", body);
    }

    [Fact]
    public async Task Unknown_video_is_404()
    {
        var client = _factory.AdminClient();
        var resp = await client.GetAsync("/videos/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private sealed record JobDto(string videoId, string status, string? mapKey);
}
