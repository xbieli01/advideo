using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.AdServing.Matching.Model;
using Xunit.Abstractions;
using static Advideo.AdServing.Matching.Tests.Fx;

namespace Advideo.AdServing.Matching.Tests;

public class GoldenAndPerformanceTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record Seed(List<PlatformConfig> Platforms, List<Campaign> Campaigns);

    private static string Repo(string rel) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", rel));

    /// <summary>
    /// Golden test: content map produced by the real M1 pipeline (mock analyzers,
    /// `python -m advideo_pipeline.cli --video x --video-id golden-mock --duration-sec 24`) + sample seed.
    /// </summary>
    [Theory]
    [InlineData("demo-portal", 3, "tesla-model-y", "tesla-300x250")]
    [InlineData("demo-portal", 9, "nike-running", "nike-728x90")]
    [InlineData("demo-portal", 15, null, null)]
    [InlineData("demo-portal", 21, null, null)]
    [InlineData("browser-extension", 3, "tesla-model-y", "tesla-300x250")]
    [InlineData("browser-extension", 9, null, null)] // Nike has only a 728x90 banner, extension slot is 300x250
    public void Pipeline_map_and_seed_produce_expected_decisions(string platformId, double t, string? campaign, string? creative)
    {
        var map = JsonSerializer.Deserialize<ContentMap>(File.ReadAllText(Repo("tests/fixtures/golden-mock.video-map.json")), Json)!;
        var seed = JsonSerializer.Deserialize<Seed>(File.ReadAllText(Repo("seed/adserving.sample.json")), Json)!;
        var platform = seed.Platforms.Single(p => p.Id == platformId);

        var d = Matcher.Decide(map.VideoId, map.FindSegment(t), seed.Campaigns, platform, Ctx());

        Assert.Equal(campaign, d.Winner?.Campaign.Id);
        Assert.Equal(creative, d.Winner?.Creative?.Id);
    }

    [Fact]
    public void Decision_latency_with_1000_campaigns()
    {
        var rnd = new Random(42);
        var cats = Tax.Categories.ToArray();
        var brands = Tax.Brands.ToArray();
        string[] terms = ["car", "road", "running", "sneakers", "pizza", "laptop", "beach", "bottle", "gym", "airplane"];
        var campaigns = Enumerable.Range(0, 1000).Select(i => Campaign($"c{i:0000}", new Targeting
        {
            Categories = [cats[rnd.Next(cats.Length)]],
            Brands = rnd.NextDouble() < 0.3 ? [brands[rnd.Next(brands.Length)]] : [],
            Keywords = [terms[rnd.Next(terms.Length)], terms[rnd.Next(terms.Length)]],
        }, cpm: 1 + rnd.Next(20), priority: 1 + rnd.Next(10))).ToList();

        var segments = Enumerable.Range(0, 50).Select(_ => Segment(
            entities: [new ContentEntity("brand", brands[rnd.Next(brands.Length)], 0.5 + rnd.NextDouble() / 2)],
            keywords: [new ScoredTerm(terms[rnd.Next(terms.Length)], rnd.NextDouble()), new ScoredTerm(terms[rnd.Next(terms.Length)], rnd.NextDouble())],
            categories: [cats[rnd.Next(cats.Length)], cats[rnd.Next(cats.Length)]])).ToList();

        var platform = Platform();
        var ctx = Ctx();
        for (var i = 0; i < 200; i++) Matcher.Decide("v", segments[i % 50], campaigns, platform, ctx); // warm-up

        var samples = new double[10_000];
        var sw = new Stopwatch();
        for (var i = 0; i < samples.Length; i++)
        {
            sw.Restart();
            Matcher.Decide("v", segments[i % 50], campaigns, platform, ctx);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        double p50 = samples[5_000], p95 = samples[9_500], p99 = samples[9_900];
        output.WriteLine($"1000 campaigns, 10k decisions: p50={p50:0.000} ms p95={p95:0.000} ms p99={p99:0.000} ms");

        Assert.True(p95 < 30, $"p95 {p95:0.000} ms exceeds the 30 ms budget");
    }
}
