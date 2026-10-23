using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>
/// A deterministic, testable gate between external search metadata and local library writes.
/// Search rank, search-term repetition, the word "official", or an audio file with valid MP3
/// bytes NEVER constitute proof that this is the correct series theme.
/// </summary>
internal static class ThemeCandidateDecisionEngine
{
    internal const int MinimumAutomaticScore = 92;
    internal const int MinimumWinningMargin = 7;

    private static readonly string[] RejectedPhrases =
    [
        "cover", "karaoke", "reaction", "review", "explained", "tutorial",
        "how to play", "piano", "guitar", "remix", "mashup", "parody",
        "fan made", "fanmade", "fan edit", "fan edit", "tribute", "slowed",
        "sped up", "nightcore", "trailer", "teaser", "gameplay", "walkthrough",
        "full episode", "full movie", "10 hours", "one hour", "1 hour",
        "extended version", "live performance", "concert", "unboxing",
        "ai generated", "ai cover", "instrumental remake", "recreation",
        "8d audio", "lofi", "lo fi", "reimagined", "custom intro",
        "alternate intro", "alternative intro", "fan opening", "fan opening credits",
        "orchestral cover", "reaction video", "ending explained", "recap",
        "ambience", "ambiance", "background music", "mix compilation", "playlist",
        "comparison", "versus", "vs", "theme evolution", "all openings",
        "season 2", "season 3", "season 4", "season 5", "ending credits",
        "credits roll", "music box", "music tutorial"
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

    private static readonly HashSet<string> AmbiguousTitles = new(StringComparer.Ordinal)
    {
        "the office", "the bridge", "the killing", "the good doctor",
        "the returned", "the prisoner", "the last of us", "the outsider",
        "the flash", "the equalizer", "the witcher", "the invisible man",
        "the lost world", "the haunting", "the twilight zone"
    };

    internal static string Normalize(string text)
    {
        var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }
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

        // The year is included in discovery to disambiguate remakes. Returned videos must
        // still pass exact independent local validation; search-engine ranking is not evidence.
        var suffix = year is > 1900 and < 2100 ? " " + year.Value.ToString(CultureInfo.InvariantCulture) : "";
        var queries = new List<string>
        {
            "\"" + titles[0] + "\" television theme song" + suffix,
            "\"" + titles[0] + "\" opening credits main title",
            titles.Length > 1
                ? "\"" + titles[1] + "\" opening theme" + suffix
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

        if (titles.Length == 0 || !titles.Any(value => ContainsPhrase(normalizedTitle, value)))
            return Reject(video, "no exact series title in the candidate title");

        if (video.Duration is not TimeSpan duration || duration.TotalSeconds < 20 || duration.TotalMinutes > 6)
            return Reject(video, "duration missing or outside 20 seconds–6 minutes");

        // A show may have remakes and films with the same name. Explicit conflicting years
        // cannot be outweighed by a convincing-looking title or uploader.
        var years = Regex.Matches(normalizedTitle, @"\b(?:19|20)\d{2}\b")
            .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToHashSet();
        if (year is > 1900 and < 2100 && years.Count > 0 && !years.Contains(year.Value))
            return Reject(video, "candidate explicitly names a different production year");

        // A multi-show comparison must not pass because it happens to contain one show name.
        foreach (var bad in RejectedPhrases)
        {
            if (ContainsPhrase(normalizedTitle, bad))
                return Reject(video, "rejected adaptation, unrelated media, or noncanonical variant: " + bad);
        }

        if (ContainsPhrase(normalizedTitle, "barber of seville")
            && titles.Any(t => ContainsPhrase(t, "knight of the seven kingdoms")))
            return Reject(video, "known incorrect Barber of Seville mapping");

        var cue = PositiveCues.FirstOrDefault(value => ContainsPhrase(normalizedTitle, value.Phrase));
        if (cue.Score == 0)
            return Reject(video, "no explicit opening/title/theme evidence");

        var matchedTitle = titles.First(value => ContainsPhrase(normalizedTitle, value));
        var titleWords = matchedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var yearMatches = year is > 1900 and < 2100 && years.Contains(year.Value);
        var ambiguous = matchedTitle.Length <= 6 || titleWords == 1 || AmbiguousTitles.Contains(matchedTitle);

        // "From" from "From Dusk Till Dawn", "Dark" from "Dark Matter", etc.:
        // independent verified show identifiers are absent from these upload searches.
        // Single-word series are therefore not auto-installed from discovery.
        if (titleWords == 1)
            return Reject(video, "single-word series cannot be safely identified by title search alone");
        if (ambiguous && !yearMatches)
            return Reject(video, "ambiguous/common title without matching year");

        var normalizedDescription = Normalize(video.Description ?? "");
        if (normalizedDescription.Length > 0 &&
            (ContainsPhrase(normalizedDescription, "fan made")
             || ContainsPhrase(normalizedDescription, "cover version")
             || ContainsPhrase(normalizedDescription, "remix")
             || ContainsPhrase(normalizedDescription, "not official")))
            return Reject(video, "description identifies a fan-made/noncanonical recording");

        var score = 50 + cue.Score;
        if (duration.TotalSeconds is >= 30 and <= 180) score += 8;
        else if (duration.TotalMinutes <= 5) score += 4;
        if (yearMatches) score += 9;
        if (ContainsPhrase(normalizedTitle, "official")) score += 3;
        if (ContainsPhrase(normalizedTitle, "tv") || ContainsPhrase(normalizedTitle, "television")
            || ContainsPhrase(normalizedTitle, "series")) score += 4;
        if (video.SearchHits > 1) score += 2;

        // "Official" is a self-asserted label, not authentication. It is only a minor bonus.
        if (ContainsPhrase(Normalize(video.Channel), "official")) score += 1;

        // Avoid using "matched two different search queries" as independent corroboration.
        // Require explicit matching year or a genuinely series-specific context.
        var hasSeriesContext =
            ContainsPhrase(normalizedTitle, "tv") ||
            ContainsPhrase(normalizedTitle, "television") ||
            ContainsPhrase(normalizedTitle, "series") ||
            ContainsPhrase(normalizedTitle, "opening credits") ||
            ContainsPhrase(normalizedTitle, "title sequence");
        if (!yearMatches && !hasSeriesContext)
            return Reject(video, "no independent year or television-series context", score);

        if (score < MinimumAutomaticScore)
            return Reject(video, "confidence below threshold (" + score + "/" + MinimumAutomaticScore + ")", score);

        return new ScoredThemeVideo(video, score, true,
            "exact series title; " + cue.Phrase + "; " + Math.Round(duration.TotalSeconds) + "s"
            + (yearMatches ? "; verified year token" : "")
            + (hasSeriesContext ? "; series-context wording" : "")
            + "; source " + video.Source);
    }

    internal static bool IsClearWinner(IReadOnlyList<ScoredThemeVideo> ranked)
        => ranked.Count > 0 && (ranked.Count == 1
            || ranked[0].Score - ranked[1].Score >= MinimumWinningMargin);

    internal static bool HasExactTitle(string seriesTitle, string? originalTitle, string candidateTitle)
    {
        var normalized = Normalize(candidateTitle);
        return new[] { seriesTitle, originalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Normalize(t!))
            .Any(t => t.Length > 0 && ContainsPhrase(normalized, t));
    }

    private static ScoredThemeVideo Reject(ThemeSearchVideo video, string reason, int score = 0)
        => new(video, score, false, reason);

    private static bool ContainsPhrase(string text, string phrase)
        => (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);
}

internal sealed record ThemeSearchVideo(
    string Id, string Title, string Channel, TimeSpan? Duration,
    int SearchHits = 1, string Source = "YouTube", Uri? Url = null,
    string? Description = null);

internal sealed record ScoredThemeVideo(
    ThemeSearchVideo Video, int Score, bool Accepted, string Reason);
