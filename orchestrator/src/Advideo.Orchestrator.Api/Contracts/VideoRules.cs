using System.Text.RegularExpressions;

namespace Advideo.Orchestrator.Api.Contracts;

/// <summary>Validation shared by single and bulk video registration.</summary>
public static partial class VideoRules
{
    public const int MaxBulkItems = 1000;

    /// <summary>
    /// Video ids become object-store keys / file names, so only safe characters are allowed
    /// (prevents path traversal such as "../../etc/x" in the file-system map store).
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex IdPattern();

    public static string? ValidateId(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return "videoId is required";
        if (!IdPattern().IsMatch(videoId) || videoId.Contains(".."))
            return "videoId may contain only letters, digits, '.', '_' and '-' (max 128, no '..')";
        return null;
    }

    /// <summary>Local path, file:// or http(s):// (incl. HLS .m3u8) — what the pipeline can read.</summary>
    public static string? ValidateUri(string? videoUri)
    {
        if (string.IsNullOrWhiteSpace(videoUri)) return "videoUri is required";
        if (videoUri.Length > 2048) return "videoUri is too long (max 2048)";
        if (Uri.TryCreate(videoUri, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1)
        {
            if (uri.Scheme is not ("http" or "https"))
                return $"unsupported scheme '{uri.Scheme}' (use a path, file:// or http(s)://; for S3 use a presigned https URL)";
        }
        return null;
    }

    [GeneratedRegex("^[a-z]{2,3}(,[a-z]{2,3})*$")]
    private static partial Regex LanguagesPattern();

    /// <summary>Normalizes "SK, cs" to "sk,cs"; null/empty = pipeline default.</summary>
    public static string? NormalizeLanguages(string? languages) =>
        string.IsNullOrWhiteSpace(languages) ? null
            : string.Join(',', languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.ToLowerInvariant()));

    /// <summary>Comma list of language-pack codes (schema/languages/&lt;code&gt;.json), e.g. "sk,cs,en", or "all".</summary>
    public static string? ValidateLanguages(string? languages)
    {
        var norm = NormalizeLanguages(languages);
        if (norm is null || norm == "all") return null;
        return LanguagesPattern().IsMatch(norm) ? null : "languages must be a comma list of language codes, e.g. \"sk,cs,en\"";
    }
}

public sealed record BulkVideoItem(string? VideoId, string? VideoUri, string? Analyzer = null, string? Track = null, string? Languages = null);

/// <summary>
/// Catalogue import. Items without analyzer/track use the batch defaults. Without <c>Force</c>,
/// videos already analyzed from the same URI are skipped (idempotent re-imports); jobs that are
/// queued/running/retrying are always skipped.
/// </summary>
public sealed record BulkRegisterRequest(
    IReadOnlyList<BulkVideoItem>? Items,
    string? Analyzer = null,
    string? Track = null,
    bool Force = false,
    string? Languages = null);

public sealed record BulkItemResult(int Index, string? VideoId, string Reason);

public sealed record BulkRegisterResponse(
    int Received,
    int Queued,
    IReadOnlyList<string> QueuedIds,
    IReadOnlyList<BulkItemResult> Skipped,
    IReadOnlyList<BulkItemResult> Invalid);
