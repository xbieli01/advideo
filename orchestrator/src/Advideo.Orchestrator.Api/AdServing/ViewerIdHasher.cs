using System.Security.Cryptography;
using System.Text;

namespace Advideo.Orchestrator.Api.AdServing;

/// <summary>
/// Pseudonymizes the anonymous viewer id sent by the snippet (HMAC-SHA256 with a server secret),
/// so raw ids are never stored. Rotating the secret resets all viewer frequency counters.
/// </summary>
public sealed class ViewerIdHasher(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public string? Hash(string? viewerId)
    {
        if (string.IsNullOrWhiteSpace(viewerId)) return null;
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(viewerId.Trim()));
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }
}
