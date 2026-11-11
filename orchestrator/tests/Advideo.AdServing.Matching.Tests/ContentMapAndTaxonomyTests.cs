using System.Text.Json;
using Advideo.AdServing.Matching.Model;
using static Advideo.AdServing.Matching.Tests.Fx;

namespace Advideo.AdServing.Matching.Tests;

public class ContentMapAndTaxonomyTests
{
    private const string MapJson = """
    {
      "schemaVersion": "1.0", "videoId": "v1", "durationSec": 15, "generatedAt": "2026-11-11T10:00:00Z",
      "segments": [
        { "startSec": 0, "endSec": 5, "keywords": [{"term":"car","score":0.8}],
          "entities": [{"type":"brand","label":"Tesla","score":0.9}], "categories": ["automotive"],
          "signals": { "objects": ["car"], "text": ["TESLA"] } },
        { "startSec": 5, "endSec": 10, "keywords": [], "entities": [], "categories": [] },
        { "startSec": 10, "endSec": 15, "keywords": [{"term":"pizza","score":0.7}], "entities": [], "categories": ["food"] }
      ]
    }
    """;

    private static ContentMap Map() => JsonSerializer.Deserialize<ContentMap>(MapJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4.99, 0)]
    [InlineData(5, 5)]
    [InlineData(14.9, 10)]
    public void FindSegment_uses_half_open_intervals(double t, double expectedStart) =>
        Assert.Equal(expectedStart, Map().FindSegment(t)!.StartSec);

    [Fact]
    public void FindSegment_outside_video_returns_null()
    {
        Assert.Null(Map().FindSegment(15));
        Assert.Null(Map().FindSegment(-1));
    }

    [Fact]
    public void Map_from_pipeline_json_drives_a_decision()
    {
        var map = Map();
        var tesla = Campaign("tesla", new Targeting { Brands = ["Tesla"] });
        Assert.True(Matcher.Decide(map.VideoId, map.FindSegment(2), [tesla], Platform(), Ctx()).HasAd);
        Assert.False(Matcher.Decide(map.VideoId, map.FindSegment(12), [tesla], Platform(), Ctx()).HasAd);
    }

    [Fact]
    public void Shared_taxonomy_is_loaded()
    {
        Assert.Contains("automotive", Tax.Categories);
        Assert.Equal("Coca-Cola", Tax.CanonicalBrand("coca cola"));
        Assert.Null(Tax.CanonicalBrand("unknown brand"));
    }

    [Fact]
    public void Campaign_validation_reports_unknown_vocabulary_and_bad_bids()
    {
        var bad = Campaign("bad", new Targeting { Categories = ["automotiv"], Brands = ["Tezla"] },
            bids: [new Bid(PricingModel.Cpm, 0m), new Bid(PricingModel.Cpm, 2m)]) with { Priority = 11 };
        var errors = Tax.Validate(bad);
        Assert.Contains(errors, e => e.Contains("automotiv"));
        Assert.Contains(errors, e => e.Contains("Tezla"));
        Assert.Contains(errors, e => e.Contains("Duplicate bid"));
        Assert.Contains(errors, e => e.Contains("positive"));
        Assert.Contains(errors, e => e.Contains("Priority"));
        Assert.Empty(Tax.Validate(Campaign("ok", new Targeting { Categories = ["automotive"], Brands = ["vw"] })));
    }
}
