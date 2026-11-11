using Advideo.Orchestrator.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Advideo.Orchestrator.Tests;

/// <summary>API keys for integration tests (configured as hashes like in production).</summary>
internal static class TestKeys
{
    public const string Admin = "test-admin-key";
    public const string Reporting = "test-reporting-key";

    public static IWebHostBuilder WithTestKeys(this IWebHostBuilder b) => b
        .UseSetting("Security:ApiKeys:0:Name", "test-admin")
        .UseSetting("Security:ApiKeys:0:Sha256", ApiKeyDefaults.Hash(Admin))
        .UseSetting("Security:ApiKeys:0:Roles:0", "admin")
        .UseSetting("Security:ApiKeys:1:Name", "test-reporting")
        .UseSetting("Security:ApiKeys:1:Sha256", ApiKeyDefaults.Hash(Reporting))
        .UseSetting("Security:ApiKeys:1:Roles:0", "reporting");

    public static HttpClient AdminClient(this WebApplicationFactory<Program> f) => f.ClientWithKey(Admin);

    public static HttpClient ClientWithKey(this WebApplicationFactory<Program> f, string? key)
    {
        var c = f.CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add(ApiKeyDefaults.Header, key);
        return c;
    }
}
