using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Strict anime-only fallback. A result is accepted only when the catalogue's title or
/// synonym exactly matches one of Jellyfin's titles (or a stable-ID override) and its year agrees.
/// It never performs a generic YouTube search.</summary>
internal sealed class AnimeThemesProvider(
    HttpClient client,
    IThemeAudioProcessor audioProcessor,
    IReadOnlyDictionary<string, string[]>? titleOverrides = null)
    : IThemeProvider
{
    private const string ApiRoot = "https://api.animethemes.moe/anime";
    private const string UserAgent = "ThemeMusicFinder/1.2";
    private const long MaxResponseBytes = 50L * 1024 * 1024;

    // These aliases were verified against AnimeThemes and are bound to stable catalogue IDs.
    // That makes them materially safer than fuzzy matching a title such as "When They Cry".
    private static readonly IReadOnlyDictionary<string, string[]> KnownTitleOverrides =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tvdb:74309"] = ["Macross II: Lovers Again"],
            ["tmdb:82247"] = ["Macross II: Lovers Again"],
            ["tvdb:303067"] = ["Norn9: Norn+Nonet"],
            ["tmdb:66120"] = ["Norn9: Norn+Nonet"],
            ["tvdb:407633"] = ["Higurashi no Naku Koro ni Gou"],
            ["tmdb:75475"] = ["Higurashi no Naku Koro ni Gou"],
            ["tvdb:435343"] = ["Zatsu Tabi: That's Journey"],
            ["tmdb:254853"] = ["Zatsu Tabi: That's Journey"]
        };

    private string? _metadataOutageReason;

    private sealed record ThemeCandidate(Uri AudioUri, string Type);

    private sealed record SearchQuery(string Value, bool IsNormalized);

    /// <summary>TVDB-keyed v1.2.1.5 misses invalidated by this matcher revision.</summary>
    internal static IReadOnlySet<string> CorrectedAttemptKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "74309", "303067", "407633", "435343" };

    internal static IReadOnlyDictionary<string, string[]> DefaultTitleOverrides
        => KnownTitleOverrides;

    internal int SearchRequestCount { get; private set; }

    internal int DetailRequestCount { get; private set; }

    internal int AudioRequestCount { get; private set; }

    internal int NormalizedQueryRequestCount { get; private set; }

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
        var titles = GetLookupTitles(series, titleOverrides);
        if (titles.Count == 0)
        {
            return ThemeFetchResult.NotApplicable("AnimeThemes lookup requires a series title");
        }

        // A 403, rate limit, timeout, or service error is provider-wide. Preserve the transient
        // outcome for every affected series (so none is negative-cached), but do not hammer the
        // same unavailable API hundreds of times during one sweep.
        if (_metadataOutageReason is not null)
        {
            return ThemeFetchResult.Transient(_metadataOutageReason);
        }

        ThemeCandidate? ed1Fallback = null;
        var matchedAny = false;
        var ambiguousMatch = false;
        var inspectedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in GetSearchQueries(titles))
        {
            if (query.IsNormalized) NormalizedQueryRequestCount++;
            var searchResponse = await GetMetadataAsync(
                BuildMetadataUri(query.Value, series.ProductionYear),
                isSearch: true,
                ct).ConfigureAwait(false);
            if (searchResponse.Failure is not null) return searchResponse.Failure;
            using var searchJson = searchResponse.Document;
            if (searchJson is null) continue;

            var slugs = FindStrictAnimeSlugs(searchJson.RootElement, titles, series.ProductionYear);
            if (slugs.Count > 1)
            {
                // Exact title+year collisions are unusual, but silently picking the first one is
                // still a fuzzy choice. Leave it unresolved for a future stable-ID override.
                ambiguousMatch = true;
                continue;
            }

            if (slugs.Count == 0 || !inspectedSlugs.Add(slugs[0])) continue;
            matchedAny = true;

            var detailResponse = await GetMetadataAsync(
                BuildThemeUri(slugs[0]),
                isSearch: false,
                ct).ConfigureAwait(false);
            if (detailResponse.Failure is not null) return detailResponse.Failure;
            using var detailJson = detailResponse.Document;
            if (detailJson is null)
            {
                return ThemeFetchResult.Transient(
                    "AnimeThemes matched a record that disappeared before its theme metadata was fetched");
            }

            var candidate = FindStrictTheme(detailJson.RootElement, titles, series.ProductionYear);
            if (candidate?.Type == "OP")
            {
                return await DownloadAsync(candidate, ct).ConfigureAwait(false);
            }

            // OP1 is always preferred. Keep a safe ED1 in reserve while trying any other exact
            // record in case it supplies an OP1 result.
            ed1Fallback ??= candidate;
        }

        if (ed1Fallback is not null)
        {
            return await DownloadAsync(ed1Fallback, ct).ConfigureAwait(false);
        }

        if (ambiguousMatch)
        {
            return ThemeFetchResult.NotFound(
                "AnimeThemes returned multiple exact title/year matches; a stable-ID override is required");
        }

        return ThemeFetchResult.NotFound(matchedAny
            ? "AnimeThemes found the exact series but no safe OP1 or ED1 audio file"
            : "AnimeThemes had no exact title/year match with a safe OP1 or ED1 audio file");
    }

    private async Task<(JsonDocument? Document, ThemeFetchResult? Failure)> GetMetadataAsync(
        Uri uri,
        bool isSearch,
        CancellationToken ct)
    {
        try
        {
            if (isSearch) SearchRequestCount++; else DetailRequestCount++;
            using var response = await client.GetAsync(uri, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _metadataOutageReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "AnimeThemes returned HTTP {0:D}",
                    response.StatusCode);
                return (null, ThemeFetchResult.Transient(_metadataOutageReason));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (
                await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false),
                null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _metadataOutageReason = "AnimeThemes metadata request timed out";
            return (null, ThemeFetchResult.Transient(_metadataOutageReason));
        }
        catch (HttpRequestException ex)
        {
            _metadataOutageReason = $"AnimeThemes metadata request failed: {ex.Message}";
            return (null, ThemeFetchResult.Transient(_metadataOutageReason));
        }
        catch (JsonException ex)
        {
            _metadataOutageReason = $"AnimeThemes returned invalid JSON: {ex.Message}";
            return (null, ThemeFetchResult.Transient(_metadataOutageReason));
        }
    }

    private async Task<ThemeFetchResult> DownloadAsync(ThemeCandidate candidate, CancellationToken ct)
    {
        try
        {
            AudioRequestCount++;
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

    /// <summary>Builds the lightweight discovery request. Theme media is fetched only after a
    /// locally validated exact match, keeping 404/miss responses small.</summary>
    internal static Uri BuildMetadataUri(string title, int? productionYear)
    {
        var query = Uri.EscapeDataString(title);
        var yearFilter = productionYear is int year
            ? $"&filter[year]={year.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return new Uri(
            $"{ApiRoot}?q={query}{yearFilter}&include=animesynonyms&page[size]=20");
    }

    internal static Uri BuildThemeUri(string slug)
        => new(
            $"{ApiRoot}/{Uri.EscapeDataString(slug)}?include=animesynonyms,animethemes.animethemeentries.videos.audio");

    internal static IReadOnlyList<string> GetLookupTitles(
        Series series,
        IReadOnlyDictionary<string, string[]>? additionalOverrides = null)
    {
        var titles = new List<string>();

        foreach (var alias in GetOverrideTitles(series, additionalOverrides)) AddTitle(alias);
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

    /// <summary>Changes the negative-cache key only for a series with an explicit stable-ID
    /// alias. Adding or editing an override therefore makes that series eligible immediately,
    /// while every unrelated confirmed miss retains its existing backoff.</summary>
    internal static string? GetOverrideCacheSuffix(
        Series series,
        IReadOnlyDictionary<string, string[]>? additionalOverrides = null)
    {
        var aliases = GetOverrideTitles(series, additionalOverrides);
        if (aliases.Count == 0) return null;
        var canonical = string.Join('\n', aliases.Select(alias => alias.Trim()).Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "anime-alias-" + Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static IReadOnlyList<string> GetOverrideTitles(
        Series series,
        IReadOnlyDictionary<string, string[]>? additionalOverrides)
    {
        var aliases = new List<string>();
        AddFrom(additionalOverrides, "tvdb", series.GetProviderId(MetadataProvider.Tvdb));
        AddFrom(additionalOverrides, "tmdb", series.GetProviderId(MetadataProvider.Tmdb));
        AddFrom(KnownTitleOverrides, "tvdb", series.GetProviderId(MetadataProvider.Tvdb));
        AddFrom(KnownTitleOverrides, "tmdb", series.GetProviderId(MetadataProvider.Tmdb));
        return aliases;

        void AddFrom(
            IReadOnlyDictionary<string, string[]>? source,
            string provider,
            string? id)
        {
            if (source is null
                || string.IsNullOrWhiteSpace(id)
                || !source.TryGetValue($"{provider}:{id}", out var values)) return;
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value)
                    || aliases.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) continue;
                aliases.Add(value.Trim());
            }
        }
    }

    internal static IReadOnlyList<string> BuildSearchQueryValues(IReadOnlyList<string> titles)
        => GetSearchQueries(titles).Select(query => query.Value).ToArray();

    private static IReadOnlyList<SearchQuery> GetSearchQueries(IReadOnlyList<string> titles)
    {
        var queries = new List<SearchQuery>();
        foreach (var title in titles)
        {
            Add(title, isNormalized: false);
            var normalizedQuery = NormalizeSearchQuery(title);
            if (!string.Equals(title, normalizedQuery, StringComparison.Ordinal))
                Add(normalizedQuery, isNormalized: true);
        }

        return queries;

        void Add(string value, bool isNormalized)
        {
            if (string.IsNullOrWhiteSpace(value)
                || queries.Any(existing => string.Equals(
                    existing.Value,
                    value,
                    StringComparison.OrdinalIgnoreCase))) return;
            queries.Add(new SearchQuery(value, isNormalized));
        }
    }

    internal static Uri? FindStrictOp1(JsonElement root, string seriesName, int? productionYear)
        => FindStrictTheme(root, [StripMatchingYearSuffix(seriesName, productionYear)], productionYear)
            is { Type: "OP" } candidate
            ? candidate.AudioUri
            : null;

    private static IReadOnlyList<string> FindStrictAnimeSlugs(
        JsonElement root,
        IReadOnlyList<string> seriesTitles,
        int? productionYear)
    {
        var wanted = GetNormalizedTitles(seriesTitles);
        return EnumerateAnime(root)
            .Where(anime => IsStrictMatch(anime, wanted, productionYear))
            .Select(anime => anime.TryGetProperty("slug", out var slug)
                && slug.ValueKind == JsonValueKind.String ? slug.GetString() : null)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ThemeCandidate? FindStrictTheme(
        JsonElement root,
        IReadOnlyList<string> seriesTitles,
        int? productionYear)
    {
        var wanted = GetNormalizedTitles(seriesTitles);
        ThemeCandidate? ed1Fallback = null;

        foreach (var anime in EnumerateAnime(root))
        {
            if (!IsStrictMatch(anime, wanted, productionYear)) continue;
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

    private static IEnumerable<JsonElement> EnumerateAnime(JsonElement root)
    {
        if (!root.TryGetProperty("anime", out var anime)) yield break;
        if (anime.ValueKind == JsonValueKind.Object)
        {
            yield return anime;
            yield break;
        }

        if (anime.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in anime.EnumerateArray()) yield return value;
    }

    private static HashSet<string> GetNormalizedTitles(IEnumerable<string> titles)
        => titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsStrictMatch(
        JsonElement anime,
        IReadOnlySet<string> wanted,
        int? productionYear)
    {
        var titles = new List<string>();
        if (anime.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            titles.Add(name.GetString()!);
        AddSynonyms(anime, "animesynonyms", titles);
        // Kept for compatibility with older fixtures/proxies.
        AddSynonyms(anime, "synonyms", titles);

        if (!titles.Any(title => wanted.Contains(NormalizeTitle(title)))) return false;
        return productionYear is not int wantedYear
            || anime.TryGetProperty("year", out var year)
            && year.ValueKind == JsonValueKind.Number
            && year.TryGetInt32(out var actualYear)
            && actualYear == wantedYear;
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

    /// <summary>Produces a search-only variant that is friendlier to AnimeThemes' full-text
    /// search. Acceptance still uses <see cref="NormalizeTitle"/>, exact aliases, and year.</summary>
    internal static string NormalizeSearchQuery(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        var previousWasLowerOrDigit = false;
        var pendingSpace = false;

        foreach (var c in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
            {
                if ((pendingSpace || char.IsUpper(c) && previousWasLowerOrDigit)
                    && builder.Length > 0
                    && builder[^1] != ' ') builder.Append(' ');
                builder.Append(c);
                pendingSpace = false;
                previousWasLowerOrDigit = char.IsLower(c) || char.IsDigit(c);
                continue;
            }

            // Apostrophes are internal word punctuation (That's -> Thats); other punctuation
            // becomes a word boundary (-That's Journey- -> Thats Journey).
            if (c is '\'' or '\u2019') continue;
            pendingSpace = builder.Length > 0;
            previousWasLowerOrDigit = false;
        }

        return builder.ToString().Trim();
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
