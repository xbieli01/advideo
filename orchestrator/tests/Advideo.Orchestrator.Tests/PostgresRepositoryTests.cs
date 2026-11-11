using Advideo.AdServing.Matching.Model;
using Advideo.Orchestrator.Core.Model;
using Advideo.Orchestrator.Infrastructure.Postgres;
using Npgsql;

namespace Advideo.Orchestrator.Tests;

/// <summary>Runs against PostgreSQL when <c>ADVIDEO_TEST_PG</c> is set (Npgsql connection string).</summary>
public class PostgresRepositoryTests
{
    private static readonly Lazy<NpgsqlDataSource?> Db = new(() =>
    {
        var cs = Environment.GetEnvironmentVariable("ADVIDEO_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var ds = NpgsqlDataSource.Create(cs);
        PostgresSchema.EnsureAsync(ds).GetAwaiter().GetResult();
        return ds;
    });

    private static NpgsqlDataSource Pg()
    {
        Skip.If(Db.Value is null, "ADVIDEO_TEST_PG not set");
        return Db.Value!;
    }

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task Campaigns_round_trip_with_enums_and_servable_filter()
    {
        var repo = new PostgresCampaignRepository(Pg());
        var active = new Campaign
        {
            Id = $"Pg-Active-{_run}", AdvertiserId = "a", Name = "n", Status = CampaignStatus.Active,
            Bids = [new Bid(PricingModel.Cpc, 1.25m)],
            Targeting = new Targeting { Brands = ["Tesla"], Platforms = [$"portal-{_run}"] },
            Creatives = [new Creative("c", "https://x/a.png", "https://x", 300, 250)],
        };
        await repo.UpsertAsync(active);
        await repo.UpsertAsync(active with { Id = $"pg-paused-{_run}", Status = CampaignStatus.Paused });
        await repo.UpsertAsync(active with { Id = $"pg-other-{_run}", Targeting = active.Targeting with { Platforms = ["elsewhere"] } });

        var loaded = await repo.GetAsync($"pg-active-{_run}"); // ids are case-insensitive
        Assert.NotNull(loaded);
        Assert.Equal(PricingModel.Cpc, loaded!.Bids[0].Model);
        Assert.Equal(1.25m, loaded.Bids[0].Amount);
        Assert.Equal(["Tesla"], loaded.Targeting.Brands);

        var servable = (await repo.ListServableAsync($"PORTAL-{_run}")).Select(c => c.Id).Where(id => id.EndsWith(_run)).ToList();
        Assert.Equal([$"Pg-Active-{_run}"], servable);

        Assert.True(await repo.DeleteAsync($"pg-active-{_run}"));
        Assert.Null(await repo.GetAsync($"pg-active-{_run}"));
        await repo.DeleteAsync($"pg-paused-{_run}");
        await repo.DeleteAsync($"pg-other-{_run}");
    }

    [SkippableFact]
    public async Task Platforms_and_jobs_round_trip()
    {
        var platforms = new PostgresPlatformRepository(Pg());
        var p = new PlatformConfig
        {
            Id = $"plat-{_run}", Name = "P", Kind = PlatformKind.BrowserExtension,
            Pricing = new PricingSettings { EnabledModels = [PricingModel.Cpm, PricingModel.Cpv] },
            Slots = [new AdSlot("side", SlotPosition.Right, 300, 250)],
        };
        await platforms.UpsertAsync(p);
        var lp = await platforms.GetAsync(p.Id);
        Assert.Equal(PlatformKind.BrowserExtension, lp!.Kind);
        Assert.Equal([PricingModel.Cpm, PricingModel.Cpv], lp.Pricing.EnabledModels);
        Assert.True(await platforms.DeleteAsync(p.Id));

        var jobs = new PostgresJobRepository(Pg());
        await jobs.UpsertAsync(new VideoJob { VideoId = $"vid-{_run}", VideoUri = "/x.mp4", Status = JobStatus.Completed, MapKey = "k" });
        var j = await jobs.GetAsync($"vid-{_run}");
        Assert.Equal((JobStatus.Completed, "k"), (j!.Status, j.MapKey));
    }
}
