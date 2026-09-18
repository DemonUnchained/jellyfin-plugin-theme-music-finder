using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Strict anime-only fallback. A result is accepted only when the catalogue's title or
/// synonym exactly matches Jellyfin's title and its year agrees. It never performs a generic
/// YouTube search.</summary>
internal sealed class AnimeThemesProvider(HttpClient client, IThemeAudioProcessor audioProcessor)
    : IThemeProvider
{
    private const string ApiRoot = "https://api.animethemes.moe/anime";
    private const long MaxResponseBytes = 50L * 1024 * 1024;

    public static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PlexThemeProvider.UserAgent);
        return http;
    }

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(series.Name))
        {
            return ThemeFetchResult.NotApplicable("AnimeThemes lookup requires a series title");
        }

        var query = Uri.EscapeDataString(series.Name);
        var metadataUrl = new Uri(
            $"{ApiRoot}?q={query}&include=animesynonyms,animethemes.animethemeentries.videos.audio&page[size]=5");
        using var response = await client.GetAsync(metadataUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ThemeFetchResult.NotFound("AnimeThemes returned 404");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ThemeFetchResult.Transient(string.Format(
                CultureInfo.InvariantCulture,
                "AnimeThemes returned HTTP {0:D}",
                response.StatusCode));
        }

        Uri? audioUri;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            audioUri = FindStrictOp1(json.RootElement, series.Name, series.ProductionYear);
        }
        catch (JsonException ex)
        {
            return ThemeFetchResult.Transient($"AnimeThemes returned invalid JSON: {ex.Message}");
        }

        if (audioUri is null)
        {
            return ThemeFetchResult.NotFound("AnimeThemes had no exact title/year match with a safe OP1 audio file");
        }

        try
        {
            using var audioResponse = await client.GetAsync(audioUri, ct).ConfigureAwait(false);
            if (!audioResponse.IsSuccessStatusCode)
            {
                return ThemeFetchResult.Transient(string.Format(
                    CultureInfo.InvariantCulture,
                    "AnimeThemes audio returned HTTP {0:D}",
                    audioResponse.StatusCode));
            }

            var source = await audioResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var mp3 = await audioProcessor
                .ConvertToNormalizedMp3Async(source, ".ogg", ct)
                .ConfigureAwait(false);
            return ThemeFile.IsValidMp3(mp3, "audio/mpeg")
                ? ThemeFetchResult.Found(mp3, "AnimeThemes", audioUri)
                : ThemeFetchResult.Transient("AnimeThemes conversion did not produce a valid MP3");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ThemeFetchResult.Transient($"AnimeThemes download or conversion failed: {ex.Message}");
        }
    }

    internal static Uri? FindStrictOp1(JsonElement root, string seriesName, int? productionYear)
    {
        if (!root.TryGetProperty("anime", out var animeArray)
            || animeArray.ValueKind != JsonValueKind.Array) return null;

        var wanted = NormalizeTitle(seriesName);
        foreach (var anime in animeArray.EnumerateArray())
        {
            var titles = new List<string>();
            if (anime.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                titles.Add(name.GetString()!);
            if (anime.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            {
                foreach (var synonym in synonyms.EnumerateArray())
                {
                    if (synonym.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        titles.Add(text.GetString()!);
                }
            }

            if (!titles.Any(title => NormalizeTitle(title) == wanted)) continue;
            if (productionYear is int wantedYear
                && (!anime.TryGetProperty("year", out var year)
                    || !year.TryGetInt32(out var actualYear)
                    || actualYear != wantedYear)) continue;

            if (!anime.TryGetProperty("animethemes", out var themes)
                || themes.ValueKind != JsonValueKind.Array) continue;
            foreach (var theme in themes.EnumerateArray())
            {
                var type = theme.TryGetProperty("type", out var typeProperty)
                    ? typeProperty.GetString()
                    : null;
                var sequence = theme.TryGetProperty("sequence", out var sequenceProperty)
                    && sequenceProperty.TryGetInt32(out var value) ? value : 1;
                if (!string.Equals(type, "OP", StringComparison.OrdinalIgnoreCase) || sequence != 1) continue;
                if (!theme.TryGetProperty("animethemeentries", out var entries)
                    || entries.ValueKind != JsonValueKind.Array) continue;

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
                        return uri;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsTrue(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;

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
