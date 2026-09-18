using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Primary provider backed by ThemerrDB's curated TMDB-to-YouTube mapping.</summary>
internal sealed class ThemerrThemeProvider(HttpClient client, IThemeAudioDownloader downloader) : IThemeProvider
{
    private const string UrlTemplate = "https://app.lizardbyte.dev/ThemerrDB/tv_shows/themoviedb/{0}.json";
    private const long MaxMetadataBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxMetadataBytes
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PlexThemeProvider.UserAgent);
        return http;
    }

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var tmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return ThemeFetchResult.NotApplicable("ThemerrDB lookup requires a TMDB ID");
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            UrlTemplate,
            Uri.EscapeDataString(tmdbId));
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ThemeFetchResult.NotFound("ThemerrDB returned 404");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ThemeFetchResult.Transient(string.Format(
                CultureInfo.InvariantCulture,
                "ThemerrDB returned HTTP {0:D}",
                response.StatusCode));
        }

        Uri? videoUri;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("id", out var idProperty)
                && idProperty.ToString() != tmdbId)
            {
                return ThemeFetchResult.Transient(
                    $"ThemerrDB returned TMDB ID {idProperty} for requested ID {tmdbId}");
            }

            if (!json.RootElement.TryGetProperty("youtube_theme_url", out var property)
                || property.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(property.GetString(), UriKind.Absolute, out videoUri)
                || !IsAllowedYoutubeUri(videoUri))
            {
                return ThemeFetchResult.NotFound("ThemerrDB entry has no valid YouTube theme URL");
            }
        }
        catch (JsonException ex)
        {
            return ThemeFetchResult.Transient($"ThemerrDB returned invalid JSON: {ex.Message}");
        }

        try
        {
            var body = await downloader.DownloadMp3Async(videoUri, ct).ConfigureAwait(false);
            return ThemeFile.IsValidMp3(body, "audio/mpeg")
                ? ThemeFetchResult.Found(body, "ThemerrDB", videoUri)
                : ThemeFetchResult.Transient("YouTube conversion did not produce a valid MP3");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ThemerrDB answered successfully and identified one specific video. A failure from
            // this point is candidate-local (deleted/restricted video, missing audio stream,
            // stalled media CDN, conversion failure), not evidence that the catalogue itself is
            // down. Let the curated AnimeThemes/Plex fallbacks try instead of blocking the chain.
            return ThemeFetchResult.CandidateUnavailable(
                $"ThemerrDB YouTube candidate was unusable: {ex.Message}");
        }
    }

    internal static bool IsAllowedYoutubeUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));
}
