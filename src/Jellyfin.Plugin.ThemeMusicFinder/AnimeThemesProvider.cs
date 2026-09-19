using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Strict anime-only fallback. A result is accepted only when the catalogue's title or
/// synonym exactly matches one of Jellyfin's titles and its year agrees. It never performs a
/// generic YouTube search.</summary>
internal sealed class AnimeThemesProvider(HttpClient client, IThemeAudioProcessor audioProcessor)
    : IThemeProvider
{
    private const string ApiRoot = "https://api.animethemes.moe/anime";
    private const string UserAgent = "ThemeMusicFinder/1.2";
    private const long MaxResponseBytes = 50L * 1024 * 1024;
    private string? _metadataOutageReason;

    private sealed record ThemeCandidate(Uri AudioUri, string Type);

    public static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        // AnimeThemes' Cloudflare rules reject the longer Plex-oriented user-agent even though
        // the same API request is accepted with a conventional product/version identifier.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var titles = GetLookupTitles(series);
        if (titles.Count == 0)
        {
            return ThemeFetchResult.NotApplicable("AnimeThemes lookup requires a series title");
        }

        // A 403, rate limit, or service error is provider-wide. Preserve the transient outcome
        // for every affected series (so none is negative-cached), but do not hammer the same
        // unavailable API hundreds of times during one sweep.
        if (_metadataOutageReason is not null)
        {
            return ThemeFetchResult.Transient(_metadataOutageReason);
        }

        ThemeCandidate? ed1Fallback = null;
        foreach (var title in titles)
        {
            var metadataUrl = BuildMetadataUri(title, series.ProductionYear);
            using var response = await client.GetAsync(metadataUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _metadataOutageReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "AnimeThemes returned HTTP {0:D}",
                    response.StatusCode);
                return ThemeFetchResult.Transient(_metadataOutageReason);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var candidate = FindStrictTheme(json.RootElement, titles, series.ProductionYear);
                if (candidate?.Type == "OP")
                {
                    return await DownloadAsync(candidate, ct).ConfigureAwait(false);
                }

                // OP1 is always preferred. Keep a safe ED1 in reserve while trying the other
                // exact title (normally OriginalTitle) in case it returns an OP1 result.
                ed1Fallback ??= candidate;
            }
            catch (JsonException ex)
            {
                return ThemeFetchResult.Transient($"AnimeThemes returned invalid JSON: {ex.Message}");
            }
        }

        return ed1Fallback is not null
            ? await DownloadAsync(ed1Fallback, ct).ConfigureAwait(false)
            : ThemeFetchResult.NotFound(
                "AnimeThemes had no exact title/year match with a safe OP1 or ED1 audio file");
    }

    private async Task<ThemeFetchResult> DownloadAsync(ThemeCandidate candidate, CancellationToken ct)
    {
        try
        {
            using var audioResponse = await client.GetAsync(candidate.AudioUri, ct).ConfigureAwait(false);
            if (!audioResponse.IsSuccessStatusCode)
            {
                return ThemeFetchResult.Transient(string.Format(
                    CultureInfo.InvariantCulture,
                    "AnimeThemes {0}1 audio returned HTTP {1:D}",
                    candidate.Type,
                    audioResponse.StatusCode));
            }

            var source = await audioResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mp3 = await audioProcessor
                .ConvertToNormalizedMp3Async(source, ".ogg", ct)
                .ConfigureAwait(false);
            return ThemeFile.IsValidMp3(mp3, "audio/mpeg")
                ? ThemeFetchResult.Found(mp3, $"AnimeThemes {candidate.Type}1", candidate.AudioUri)
                : ThemeFetchResult.Transient(
                    $"AnimeThemes {candidate.Type}1 conversion did not produce a valid MP3");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ThemeFetchResult.Transient(
                $"AnimeThemes {candidate.Type}1 download or conversion failed: {ex.Message}");
        }
    }

    internal static Uri BuildMetadataUri(string title, int? productionYear)
    {
        var query = Uri.EscapeDataString(title);
        var yearFilter = productionYear is int year
            ? $"&filter[year]={year.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        var pageSize = productionYear.HasValue ? 5 : 20;
        return new Uri(
            $"{ApiRoot}?q={query}{yearFilter}&include=animesynonyms,animethemes.animethemeentries.videos.audio&page[size]={pageSize}");
    }

    internal static IReadOnlyList<string> GetLookupTitles(Series series)
    {
        var titles = new List<string>();
        AddTitle(series.Name);
        AddTitle(series.OriginalTitle);
        return titles;

        void AddTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var title = StripMatchingYearSuffix(value.Trim(), series.ProductionYear);
            if (title.Length == 0) return;
            var normalized = NormalizeTitle(title);
            if (titles.Any(existing => NormalizeTitle(existing) == normalized)) return;
            titles.Add(title);
        }
    }

    internal static Uri? FindStrictOp1(JsonElement root, string seriesName, int? productionYear)
        => FindStrictTheme(root, [StripMatchingYearSuffix(seriesName, productionYear)], productionYear)
            is { Type: "OP" } candidate
            ? candidate.AudioUri
            : null;

    private static ThemeCandidate? FindStrictTheme(
        JsonElement root,
        IReadOnlyList<string> seriesTitles,
        int? productionYear)
    {
        if (!root.TryGetProperty("anime", out var animeArray)
            || animeArray.ValueKind != JsonValueKind.Array) return null;

        var wanted = seriesTitles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal);
        ThemeCandidate? ed1Fallback = null;

        foreach (var anime in animeArray.EnumerateArray())
        {
            var titles = new List<string>();
            if (anime.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                titles.Add(name.GetString()!);
            AddSynonyms(anime, "animesynonyms", titles);
            // Kept for compatibility with older fixtures/proxies. The live API field is
            // `animesynonyms`, matching the include used above.
            AddSynonyms(anime, "synonyms", titles);

            if (!titles.Any(title => wanted.Contains(NormalizeTitle(title)))) continue;
            if (productionYear is int wantedYear
                && (!anime.TryGetProperty("year", out var year)
                    || year.ValueKind != JsonValueKind.Number
                    || !year.TryGetInt32(out var actualYear)
                    || actualYear != wantedYear)) continue;

            if (!anime.TryGetProperty("animethemes", out var themes)
                || themes.ValueKind != JsonValueKind.Array) continue;

            foreach (var theme in themes.EnumerateArray())
            {
                var type = theme.TryGetProperty("type", out var typeProperty)
                    && typeProperty.ValueKind == JsonValueKind.String
                    ? typeProperty.GetString()
                    : null;
                if (!string.Equals(type, "OP", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "ED", StringComparison.OrdinalIgnoreCase)) continue;

                var sequence = theme.TryGetProperty("sequence", out var sequenceProperty)
                    && sequenceProperty.ValueKind == JsonValueKind.Number
                    && sequenceProperty.TryGetInt32(out var value) ? value : 1;
                if (sequence != 1) continue;

                var candidate = FindSafeAudio(theme, type!.ToUpperInvariant());
                if (candidate is null) continue;
                if (candidate.Type == "OP") return candidate;
                ed1Fallback ??= candidate;
            }
        }

        return ed1Fallback;
    }

    private static ThemeCandidate? FindSafeAudio(JsonElement theme, string type)
    {
        if (!theme.TryGetProperty("animethemeentries", out var entries)
            || entries.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in entries.EnumerateArray())
        {
            if (IsTrue(entry, "nsfw") || IsTrue(entry, "spoiler")) continue;
            if (!entry.TryGetProperty("videos", out var videos)
                || videos.ValueKind != JsonValueKind.Array) continue;
            foreach (var video in videos.EnumerateArray())
            {
                if (!video.TryGetProperty("audio", out var audio)
                    || audio.ValueKind != JsonValueKind.Object
                    || !audio.TryGetProperty("link", out var link)
                    || link.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("a.animethemes.moe", StringComparison.OrdinalIgnoreCase)) continue;
                return new ThemeCandidate(uri, type);
            }
        }

        return null;
    }

    private static void AddSynonyms(JsonElement anime, string propertyName, ICollection<string> titles)
    {
        if (!anime.TryGetProperty(propertyName, out var synonyms)
            || synonyms.ValueKind != JsonValueKind.Array) return;
        foreach (var synonym in synonyms.EnumerateArray())
        {
            if (synonym.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                titles.Add(text.GetString()!);
        }
    }

    private static bool IsTrue(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;

    internal static string StripMatchingYearSuffix(string value, int? productionYear)
    {
        if (productionYear is not int year) return value;
        var suffix = $"({year.ToString(CultureInfo.InvariantCulture)})";
        if (!value.EndsWith(suffix, StringComparison.Ordinal)) return value;
        return value[..^suffix.Length].TrimEnd();
    }

    private static string NormalizeTitle(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
