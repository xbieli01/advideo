using Advideo.AdServing.Matching.Model;

namespace Advideo.AdServing.Matching;

/// <summary>
/// Pure, side-effect free ad decision: segment + campaigns + platform config + live counters → winner.
/// No I/O, so it is fast, deterministic and fully unit-testable.
/// </summary>
public sealed class AdMatcher(Taxonomy taxonomy)
{
    public AdDecision Decide(
        string videoId,
        ContentSegment? segment,
        IEnumerable<Campaign> campaigns,
        PlatformConfig platform,
        DeliveryContext ctx)
    {
        if (segment is null) return new AdDecision(null, [], "No content segment for this time.");

        var freq = platform.Frequency;
        if (ctx.AdsShownInVideo >= freq.MaxPerVideo)
            return new AdDecision(null, [], $"Platform limit {freq.MaxPerVideo} ads per video reached.");
        if (freq.MaxPerViewerPerDay is { } perDay && ctx.AdsShownToViewerToday >= perDay)
            return new AdDecision(null, [], $"Platform limit {perDay} ads per viewer per day reached.");
        if (ctx.LastAdAtSec is { } last && segment.StartSec - last < freq.MinIntervalSec && segment.EndSec - last < freq.MinIntervalSec)
            return new AdDecision(null, [], $"Minimum interval {freq.MinIntervalSec}s between ads not reached.");

        if (Violates(segment, platform.BrandSafety.ExcludeCategories, platform.BrandSafety.ExcludeKeywords) is { } unsafeReason)
            return new AdDecision(null, [], $"Platform brand safety: {unsafeReason}.");

        var candidates = campaigns.Select(c => Evaluate(c, videoId, segment, platform, ctx)).ToList();

        var eligible = candidates
            .Where(c => c.RejectedBecause is null)
            .OrderByDescending(c => c.Rank)
            .ThenBy(c => c.Campaign.Id, StringComparer.Ordinal)
            .ToList();

        var winner = eligible.FirstOrDefault();
        var ordered = eligible.Concat(candidates.Where(c => c.RejectedBecause is not null)
                .OrderBy(c => c.Campaign.Id, StringComparer.Ordinal))
            .ToList();
        return new AdDecision(winner, ordered, winner is null ? "No campaign matched this segment." : null);
    }

    private Candidate Evaluate(Campaign c, string videoId, ContentSegment seg, PlatformConfig platform, DeliveryContext ctx)
    {
        var state = ctx.StateOf(c.Id);
        Candidate Reject(string why, double relevance = 0, IReadOnlyList<MatchHit>? hits = null) =>
            new(c, relevance, 0m, 0, hits ?? [], null, null, why);

        var t = c.Targeting;
        if (c.Status != CampaignStatus.Active) return Reject($"Status {c.Status}.");
        if (c.StartAt is { } s && ctx.Now < s) return Reject("Not started yet.");
        if (c.EndAt is { } e && ctx.Now >= e) return Reject("Already ended.");
        if (t.Platforms.Count > 0 && !t.Platforms.Contains(platform.Id, StringComparer.OrdinalIgnoreCase)) return Reject("Not targeted to this platform.");
        if (t.IncludeVideos.Count > 0 && !t.IncludeVideos.Contains(videoId)) return Reject("Video not in include list.");
        if (t.ExcludeVideos.Contains(videoId)) return Reject("Video excluded.");
        if (t.Geo.Count > 0 && (ctx.Geo is null || !t.Geo.Contains(ctx.Geo, StringComparer.OrdinalIgnoreCase))) return Reject("Geo not targeted.");
        if (c.Budget.Total is { } total && state.SpentTotal >= total) return Reject("Total budget spent.");
        if (c.Budget.Daily is { } daily && state.SpentToday >= daily) return Reject("Daily budget spent.");
        if (c.FrequencyCap.PerViewerPerVideo is { } pv && state.ViewerImpressionsInVideo >= pv) return Reject("Viewer cap per video reached.");
        if (c.FrequencyCap.PerViewerPerDay is { } pd && state.ViewerImpressionsToday >= pd) return Reject("Viewer cap per day reached.");
        if (c.ExclusivityKey is not null && ctx.PreviousCampaignId is { } prevId && prevId != c.Id
            && string.Equals(ctx.PreviousExclusivityKey, c.ExclusivityKey, StringComparison.OrdinalIgnoreCase))
            return Reject("Competitor in the same exclusivity group was shown just before.");
        if (Violates(seg, t.ExcludeCategories, t.ExcludeKeywords) is { } why) return Reject($"Campaign exclusion: {why}.");

        var (relevance, hits) = Relevance(t, seg, platform.Matching.Weights);
        if (hits.Count == 0) return Reject("No targeting match.");
        if (relevance < platform.Matching.RelevanceThreshold) return Reject($"Relevance {relevance:0.00} below threshold.", relevance, hits);

        if (Pricing.BestBid(c, platform, state) is not { } best) return Reject("No bid in a pricing model enabled on this platform.", relevance, hits);

        var (creative, slot) = PickCreative(c.Creatives, platform.Slots);
        if (creative is null) return Reject("No creative fits the platform banner slots.", relevance, hits);

        double priorityFactor = 0.5 + c.Priority / 10.0;
        double rank = relevance * (double)best.Ecpm * priorityFactor * PacingFactor(c, state, ctx.Now);
        return new Candidate(c, relevance, best.Ecpm, rank, hits, creative, slot, null);
    }

    /// <summary>Weighted sum of matches × detection confidence, capped at 1.</summary>
    internal (double Relevance, IReadOnlyList<MatchHit> Hits) Relevance(Targeting t, ContentSegment seg, MatchWeights w)
    {
        var hits = new List<MatchHit>();
        var min = t.MinScore;

        var wantedBrands = t.Brands.Select(b => taxonomy.CanonicalBrand(b) ?? b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in seg.Entities.Where(e => e.Score >= min))
        {
            if (e.Type.Equals("brand", StringComparison.OrdinalIgnoreCase))
            {
                var label = taxonomy.CanonicalBrand(e.Label) ?? e.Label;
                if (wantedBrands.Contains(label)) hits.Add(new(MatchKind.Brand, label, e.Score, w.Brand * e.Score));
            }
            else if (t.Entities.Any(x => x.Type.Equals(e.Type, StringComparison.OrdinalIgnoreCase) && x.Label.Equals(e.Label, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(new(MatchKind.Entity, e.Label, e.Score, w.Entity * e.Score));
            }
        }

        var wantedKw = t.Keywords.Select(Norm).ToHashSet();
        if (wantedKw.Count > 0)
        {
            var seen = new HashSet<string>();
            foreach (var k in seg.Keywords.Where(k => k.Score >= min))
                if (wantedKw.Contains(Norm(k.Term)) && seen.Add(Norm(k.Term)))
                    hits.Add(new(MatchKind.Keyword, k.Term, k.Score, w.Keyword * k.Score));
            // OCR / audio text without a score counts with neutral confidence 0.5
            foreach (var txt in (seg.Signals?.Text ?? []).Concat(seg.Signals?.AudioKeywords ?? []))
                if (wantedKw.Contains(Norm(txt)) && seen.Add(Norm(txt)))
                    hits.Add(new(MatchKind.Keyword, txt, 0.5, w.Keyword * 0.5));
        }

        var wantedCat = t.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in seg.Categories.Distinct(StringComparer.OrdinalIgnoreCase))
            if (wantedCat.Contains(cat)) hits.Add(new(MatchKind.Category, cat, 1.0, w.Category));

        var relevance = Math.Min(1.0, hits.Sum(h => h.Contribution));
        return (relevance, hits);
    }

    /// <summary>Slow down a campaign that spends its daily budget faster than the day elapses.</summary>
    internal static double PacingFactor(Campaign c, CampaignDeliveryState s, DateTimeOffset now)
    {
        if (c.Budget.Daily is not { } daily || daily <= 0) return 1.0;
        double dayFraction = Math.Max(now.UtcDateTime.TimeOfDay.TotalHours / 24.0, 1.0 / 24);
        double spentFraction = (double)(s.SpentToday / daily);
        if (spentFraction <= dayFraction) return 1.0;
        return Math.Clamp(dayFraction / spentFraction, 0.1, 1.0);
    }

    /// <summary>Largest creative that fits one of the platform slots (exact size preferred).</summary>
    internal static (Creative?, AdSlot?) PickCreative(IReadOnlyList<Creative> creatives, IReadOnlyList<AdSlot> slots)
    {
        var options = from cr in creatives
                      from sl in slots
                      where cr.Width <= sl.Width && cr.Height <= sl.Height
                      let exact = cr.Width == sl.Width && cr.Height == sl.Height
                      orderby exact descending, cr.Width * cr.Height descending, cr.Id
                      select (cr, sl);
        var best = options.FirstOrDefault();
        return best == default ? (null, null) : (best.cr, best.sl);
    }

    private static string? Violates(ContentSegment seg, IReadOnlyList<string> excludeCategories, IReadOnlyList<string> excludeKeywords)
    {
        var cat = seg.Categories.FirstOrDefault(c => excludeCategories.Contains(c, StringComparer.OrdinalIgnoreCase));
        if (cat is not null) return $"category '{cat}'";
        if (excludeKeywords.Count == 0) return null;
        var bad = excludeKeywords.Select(Norm).ToHashSet();
        var terms = seg.Keywords.Select(k => k.Term)
            .Concat(seg.Entities.Select(e => e.Label))
            .Concat(seg.Signals?.Text ?? [])
            .Concat(seg.Signals?.AudioKeywords ?? []);
        var hit = terms.FirstOrDefault(x => bad.Contains(Norm(x)));
        return hit is null ? null : $"keyword '{hit}'";
    }

    private static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
