using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Advideo.Orchestrator.Api.Security;

/// <summary>One API key: only its SHA-256 hash is configured, never the key itself.</summary>
public sealed class ApiKeyEntry
{
    public string Name { get; set; } = "";
    /// <summary>Lowercase hex SHA-256 of the key (<c>printf %s "$KEY" | sha256sum</c>).</summary>
    public string Sha256 { get; set; } = "";
    /// <summary><c>admin</c> (full management) and/or <c>reporting</c> (read-only lists and stats).</summary>
    public List<string> Roles { get; set; } = [];
}

public sealed class ApiKeyOptions
{
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";
    public const string AdminPolicy = "admin";
    public const string ReportingPolicy = "reporting";

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

/// <summary>Authenticates <c>X-Api-Key</c> against configured key hashes (constant-time comparison).</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiKeyOptions> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.Header, out var values) || string.IsNullOrWhiteSpace(values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = Convert.FromHexString(ApiKeyDefaults.Hash(values.ToString()));
        foreach (var k in keys.CurrentValue.ApiKeys)
        {
            byte[] expected;
            try { expected = Convert.FromHexString(k.Sha256); }
            catch (FormatException) { continue; }
            if (!CryptographicOperations.FixedTimeEquals(presented, expected)) continue;

            var claims = new List<Claim> { new(ClaimTypes.Name, k.Name) };
            claims.AddRange(k.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyDefaults.Scheme)));
        }
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.Header}\"";
        return Task.CompletedTask;
    }
}
