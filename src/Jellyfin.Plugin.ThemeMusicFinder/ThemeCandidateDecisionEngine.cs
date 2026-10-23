using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Deterministic, conservative decisions based on independent search metadata.
/// A search rank or a matching series name alone NEVER authorizes a download. The returned
/// reasons make both acceptance and rejection testable without an external search service.</summary>
internal static class ThemeCandidateDecisionEngine
{
    internal const int MinimumAutomaticScore = 78;

    private static readonly string[] RejectedPhrases =
    [
        "cover", "karaoke", "reaction", "review", "explained", "tutorial",
        "how to play", "piano", "guitar", "remix", "mashup", "parody",
        "fan made", "fanmade", "fan edit", "tribute", "slowed", "sped up",
        "nightcore", "trailer", "teaser", "gameplay", "walkthrough",
        "full episode", "full movie", "10 hours", "one hour", "1 hour",
        "extended version", "live performance", "concert", "unboxing",
        "ai generated", "ai cover", "instrumental remake", "recreation"
    ];

    private static readonly (string Phrase, int Score)[] PositiveCues =
    [
        ("main title theme", 32),
        ("official theme song", 31),
        ("opening credits theme", 31),
        ("opening title sequence", 30),
        ("main title sequence", 30),
        ("main theme", 28),
        ("main title", 28),
        ("theme song", 28),
        ("theme music", 28),
        ("opening credits", 28),
        ("opening theme", 28),
        ("original theme", 28),
        ("title sequence", 27),
        ("opening titles", 27),
        ("intro theme", 27),
        ("tv theme", 27),
        ("television theme", 27),
        ("opening", 16),
        ("intro", 16)
    ];

    internal static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }

        // Match whole normalized phrases, not arbitrary substrings: "From" must not match
        // "From Dusk Till Dawn", and "You" must not match "Thank You".
        return Regex.Replace(builder.ToString(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    internal static string GetIdentifierlessAttemptKey(string title, int year)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(title) + "|" + year.ToString(CultureInfo.InvariantCulture)));
        return "title:" + Convert.ToHexStringLower(bytes);
    }

    internal static IReadOnlyList<string> BuildQueries(string title, string? originalTitle, int? year)
    {
        var titles = new[] { title, originalTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2).ToArray();
        if (titles.Length == 0) return [];

        // Deliberately bounded: a third query is more useful than searching dozens of
        // near-identical variations. A second original title can expose regional releases.
        var queries = new List<string>
        {
            "\"" + titles[0] + "\" television series theme song",
            "\"" + titles[0] + "\" opening credits main title",
            titles.Length > 1
                ? "\"" + titles[1] + "\" tv opening theme"
                : "\"" + titles[0] + "\" original tv main theme"
        };
        return queries.Select(value => value.Length <= 190 ? value : value[..190]).ToArray();
    }

    internal static ScoredThemeVideo Evaluate(
        string title, string? originalTitle, int? year, ThemeSearchVideo video)
    {
        var normalizedTitle = Normalize(video.Title);
        var titles = new[] { title, originalTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalize(value!))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();

        if (!titles.Any(value => ContainsPhrase(normalizedTitle, value)))
            return Reject(video, "video title does not contain the exact series title");

        if (video.Duration is not TimeSpan duration || duration.TotalSeconds < 20 || duration.TotalMinutes > 6)
            return Reject(video, "duration missing or outside 20 seconds–6 minutes");

        foreach (var bad in RejectedPhrases)
        {
            if (ContainsPhrase(normalizedTitle, bad))
                return Reject(video, "rejected adaptation, unrelated media, or misleading cue: " + bad);
        }

        var cue = PositiveCues.FirstOrDefault(value => ContainsPhrase(normalizedTitle, value.Phrase));
        if (cue.Score == 0)
            return Reject(video, "no explicit title/opening/theme-song evidence");

        var matchedTitle = titles.First(value => ContainsPhrase(normalizedTitle, value));
        var ambiguousTitle = matchedTitle.Length <= 6 || matchedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1
            || matchedTitle is "the office" or "the good doctor" or "the bridge" or "the killing";
        var matchedYear = year is > 1900 and < 2100
            && ContainsPhrase(normalizedTitle, year.Value.ToString(CultureInfo.InvariantCulture));

        // A common or one-word series title without a year is not enough to tell apart
        // remakes, films, songs, and similarly named series.
        if (matchedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1)
            return Reject(video, "single-word series title is unsafe for automatic matching");

        if (ambiguousTitle && !matchedYear)
            return Reject(video, "ambiguous short/common series title without matching production year");

        var score = 50 + cue.Score;
        if (duration.TotalSeconds is >= 30 and <= 180) score += 8;
        else if (duration.TotalMinutes <= 5) score += 4;
        if (matchedYear) score += 6;
        if (ContainsPhrase(normalizedTitle, "official")) score += 5;
        if (ContainsPhrase(normalizedTitle, "tv") || ContainsPhrase(normalizedTitle, "television")
            || ContainsPhrase(normalizedTitle, "series")) score += 4;
        if (video.SearchHits > 1) score += 3;
        // Channel name is a weak signal, not proof of authenticity.
        if (ContainsPhrase(Normalize(video.Channel), "official")) score += 3;

        // One convincing-looking title can still be a misleading upload. Require independent
        // corroboration from the year, repeated discovery, or explicit TV/official context.
        if (!matchedYear && video.SearchHits < 2
            && !ContainsPhrase(normalizedTitle, "official")
            && !ContainsPhrase(normalizedTitle, "tv")
            && !ContainsPhrase(normalizedTitle, "television")
            && !ContainsPhrase(normalizedTitle, "series"))
            return Reject(video, "single uncorroborated search result", score);

        if (score < MinimumAutomaticScore)
            return Reject(video, "insufficient corroborating metadata (" + score + "/" + MinimumAutomaticScore + ")", score);

        return new ScoredThemeVideo(video, score, true,
            "exact series title; " + cue.Phrase + "; " + Math.Round(duration.TotalSeconds) + "s"
            + (matchedYear ? "; production year matched" : string.Empty)
            + (video.SearchHits > 1 ? "; repeated across queries" : string.Empty));
    }

    private static ScoredThemeVideo Reject(ThemeSearchVideo video, string reason, int score = 0)
        => new(video, score, false, reason);

    private static bool ContainsPhrase(string text, string phrase)
        => (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);
}

internal sealed record ThemeSearchVideo(
    string Id, string Title, string Channel, TimeSpan? Duration, int SearchHits = 1);

internal sealed record ScoredThemeVideo(
    ThemeSearchVideo Video, int Score, bool Accepted, string Reason);
