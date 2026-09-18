using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class PlexThemeProvider(HttpClient client, IThemeAudioProcessor? audioProcessor = null) : IThemeProvider
{
    private const string UrlTemplate = "https://tvthemes.plexapp.com/{0}.mp3";

    /// <summary>Identifies the plugin to the upstream so its operator can see who is calling
    /// and reach us if we misbehave. It is a free service with no SLA.</summary>
    public const string UserAgent = "Jellyfin-ThemeMusicFinder/1.2 (+https://github.com/DemonUnchained/jellyfin-plugin-theme-music-finder)";

    // Plex currently maps A Knight of the Seven Kingdoms (TVDB 433631) to the unrelated
    // production-library track "Barber of Seville". Pin both the ID and content hash so a
    // corrected upstream file immediately starts working without a plugin update.
    private const string KnownBadKnightThemeSha256 =
        "4e3885fd0c2662a9cffb13caacd47827f1ffbe19cbb22487487a81755d266940";

    /// <summary>A theme is a few hundred KB. Anything past this is not a theme, and buffering it
    /// into a NAS's memory before validation is how a misbehaving response becomes an OOM.</summary>
    private const long MaxResponseBytes = 8L * 1024 * 1024;

    /// <summary>Below this, a request is not making progress and is holding up the whole sweep;
    /// the default 100 s across a few hundred series is hours of stalled gate.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Single construction site for the upstream client, so the timeout, response cap
    /// and User-Agent cannot drift between the scheduled task and the item-added listener.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var tvdbId = series.GetProviderId(MetadataProvider.Tvdb);
        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return ThemeFetchResult.NotApplicable("Plex lookup requires a TVDB ID");
        }

        var escapedId = Uri.EscapeDataString(tvdbId);
        var url = string.Format(CultureInfo.InvariantCulture, UrlTemplate, escapedId);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The only status that means "this series has no theme". Everything else — 5xx, 429,
            // 403, a redirect loop — is the service being unwell, not a statement about coverage.
            return ThemeFetchResult.NotFound("upstream returned 404");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ThemeFetchResult.Transient(
                string.Format(CultureInfo.InvariantCulture, "upstream returned HTTP {0:D}", response.StatusCode));
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (ThemeFile.IsValidMp3(body, contentType))
        {
            if (IsKnownIncorrectMapping(tvdbId, Convert.ToHexStringLower(SHA256.HashData(body))))
            {
                return ThemeFetchResult.NotFound(
                    "Plex returned a known incorrect 'Barber of Seville' mapping");
            }

            if (audioProcessor is not null)
            {
                try
                {
                    body = await audioProcessor
                        .ConvertToNormalizedMp3Async(body, ".mp3", ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return ThemeFetchResult.Transient($"Plex theme normalization failed: {ex.Message}");
                }
            }

            return ThemeFetchResult.Found(body, "Plex", new Uri(url));
        }

        // A 200 carrying something that is not an MP3 means a truncated download, a CDN error
        // page, or a captive portal. None of those prove the theme does not exist, so this must
        // not earn a multi-day backoff.
        return ThemeFetchResult.Transient(string.Format(
            CultureInfo.InvariantCulture,
            "upstream returned HTTP 200 with {0} bytes of '{1}', which is not a valid MP3",
            body.Length,
            contentType ?? "no content-type"));
    }

    internal static bool IsKnownIncorrectMapping(string tvdbId, string sha256)
        => tvdbId == "433631"
            && sha256.Equals(KnownBadKnightThemeSha256, StringComparison.OrdinalIgnoreCase);
}
