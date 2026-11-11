using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.AdServing.Matching.Model;

namespace Advideo.AdServing.Matching;

/// <summary>Shared vocabulary (schema/taxonomy.json) — same file as the Python pipeline uses.</summary>
public sealed class Taxonomy
{
    private readonly Dictionary<string, string> _brandByAlias;

    public IReadOnlySet<string> Categories { get; }
    public IReadOnlyList<string> Brands { get; }

    private Taxonomy(IEnumerable<string> categories, IEnumerable<BrandDoc> brands)
    {
        Categories = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        var brandList = brands.ToList();
        Brands = brandList.Select(b => b.Label).ToList();
        _brandByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in brandList)
        {
            _brandByAlias[b.Label] = b.Label;
            foreach (var a in b.Aliases) _brandByAlias[a] = b.Label;
        }
    }

    public static Taxonomy Load(string path)
    {
        using var fs = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<TaxonomyDoc>(fs, JsonOptions)
                  ?? throw new InvalidDataException($"Empty taxonomy: {path}");
        return new Taxonomy(doc.Categories, doc.Brands);
    }

    /// <summary>Loads taxonomy.json copied next to the assembly (see csproj).</summary>
    public static Taxonomy LoadDefault() =>
        Load(Path.Combine(AppContext.BaseDirectory, "taxonomy.json"));

    /// <summary>Canonical brand label for an alias ("vw" → "VW"), or null if unknown.</summary>
    public string? CanonicalBrand(string aliasOrLabel) =>
        _brandByAlias.TryGetValue(aliasOrLabel.Trim(), out var l) ? l : null;

    /// <summary>Validation errors for a campaign's targeting (unknown categories/brands).</summary>
    public IReadOnlyList<string> Validate(Campaign campaign)
    {
        var errors = new List<string>();
        var t = campaign.Targeting;
        foreach (var c in t.Categories.Concat(t.ExcludeCategories))
            if (!Categories.Contains(c)) errors.Add($"Unknown category '{c}'.");
        foreach (var b in t.Brands)
            if (CanonicalBrand(b) is null) errors.Add($"Unknown brand '{b}'.");
        if (campaign.Bids.Count == 0) errors.Add("Campaign has no bid.");
        if (campaign.Bids.GroupBy(b => b.Model).Any(g => g.Count() > 1)) errors.Add("Duplicate bid model.");
        if (campaign.Bids.Any(b => b.Amount <= 0)) errors.Add("Bid amount must be positive.");
        if (campaign.Priority is < 1 or > 10) errors.Add("Priority must be 1–10.");
        if (campaign.Creatives.Count == 0) errors.Add("Campaign has no creative.");
        return errors;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TaxonomyDoc(List<string> Categories, List<BrandDoc> Brands);

    private sealed record BrandDoc(string Label, List<string> Categories, List<string> Aliases);
}
