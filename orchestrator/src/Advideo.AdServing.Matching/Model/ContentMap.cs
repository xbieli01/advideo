namespace Advideo.AdServing.Matching.Model;

/// <summary>JSON content map produced by the M1 pipeline (schema/video-map.schema.json).</summary>
public sealed record ContentMap(
    string SchemaVersion,
    string VideoId,
    double DurationSec,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ContentSegment> Segments)
{
    /// <summary>Segment covering playback time <paramref name="t"/> (binary search; segments are time-ordered).</summary>
    public ContentSegment? FindSegment(double t)
    {
        int lo = 0, hi = Segments.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var s = Segments[mid];
            if (t < s.StartSec) hi = mid - 1;
            else if (t >= s.EndSec) lo = mid + 1;
            else return s;
        }
        return null;
    }
}

public sealed record ContentSegment(
    double StartSec,
    double EndSec,
    IReadOnlyList<ScoredTerm> Keywords,
    IReadOnlyList<ContentEntity> Entities,
    IReadOnlyList<string> Categories,
    SegmentSignals? Signals = null);

public sealed record ScoredTerm(string Term, double Score);

public sealed record ContentEntity(string Type, string Label, double Score);

public sealed record SegmentSignals(
    IReadOnlyList<string>? Objects = null,
    IReadOnlyList<string>? Actions = null,
    string? Scene = null,
    IReadOnlyList<string>? Text = null,
    IReadOnlyList<string>? AudioKeywords = null);
