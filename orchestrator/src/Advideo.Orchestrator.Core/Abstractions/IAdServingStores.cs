using Advideo.AdServing.Matching.Model;

namespace Advideo.Orchestrator.Core.Abstractions;

/// <summary>Platform (deployment) configurations: customer portals and the browser extension.</summary>
public interface IPlatformRepository
{
    Task<PlatformConfig?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformConfig>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(PlatformConfig platform, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Advertiser campaigns.</summary>
public interface ICampaignRepository
{
    Task<Campaign?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Campaign>> ListAsync(CancellationToken ct = default);
    /// <summary>Campaigns that may be served on the platform (active + targeted to it or to all platforms).</summary>
    Task<IReadOnlyList<Campaign>> ListServableAsync(string platformId, CancellationToken ct = default);
    Task UpsertAsync(Campaign campaign, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Parsed content maps for ad decisions (cached; maps change only when a video is re-analyzed).</summary>
public interface IContentMapProvider
{
    Task<ContentMap?> GetAsync(string videoId, CancellationToken ct = default);
    void Invalidate(string videoId);
}
