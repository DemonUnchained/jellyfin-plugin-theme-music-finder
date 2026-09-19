using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Loads administrator-curated stable-ID-to-YouTube mappings. This is intentionally an
/// exact ID map rather than a title search: ambiguous series names can never select a video.</summary>
internal sealed class CuratedThemeSourceStore(
    IReadOnlyDictionary<string, Uri> mappings)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal IReadOnlyDictionary<string, Uri> Mappings { get; } = mappings;

    internal static async Task<CuratedThemeSourceStore> LoadOrCreateAsync(
        string path,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteEmptyFileAtomicAsync(path, ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Created curated theme-source override file at {Path}.",
                    path);
            }

            Dictionary<string, string?> loaded;
            await using (var input = File.OpenRead(path))
            {
                loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string?>>(
                    input,
                    JsonOptions,
                    ct).ConfigureAwait(false) ?? [];
            }

            var validated = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            var rejected = 0;
            foreach (var (key, value) in loaded)
            {
                if (!IsValidKey(key)
                    || !TryNormalizeYoutubeVideoUri(value, out var source))
                {
                    rejected++;
                    continue;
                }

                validated[key.Trim()] = source;
            }

            logger.LogInformation(
                "Loaded {Count} curated stable-ID theme-source override(s) from {Path}.",
                validated.Count,
                path);
            if (rejected > 0)
            {
                logger.LogWarning(
                    "Ignored {Count} invalid curated theme-source override(s) in {Path}; keys must be tvdb:ID or tmdb:ID and values must identify one HTTPS YouTube video.",
                    rejected,
                    path);
            }

            return new CuratedThemeSourceStore(validated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(
                ex,
                "Could not load curated theme-source overrides from {Path}; continuing with the public providers.",
                path);
            return new CuratedThemeSourceStore(
                new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase));
        }
    }

    internal bool TryGetSource(Series series, out Uri source)
    {
        var tvdbId = series.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrWhiteSpace(tvdbId)
            && Mappings.TryGetValue($"tvdb:{tvdbId}", out source!))
        {
            return true;
        }

        var tmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(tmdbId)
            && Mappings.TryGetValue($"tmdb:{tmdbId}", out source!))
        {
            return true;
        }

        source = null!;
        return false;
    }

    /// <summary>Adding or changing one curated URL makes only that stable ID eligible
    /// immediately, without clearing unrelated negative-cache records.</summary>
    internal string? GetCacheSuffix(Series series)
    {
        if (!TryGetSource(series, out var source)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri));
        return "source-override-" + Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    internal static bool TryNormalizeYoutubeVideoUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string? videoId = null;
        if (candidate.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = candidate.AbsolutePath.Trim('/').Split('/', 2)[0];
        }
        else if (candidate.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                 || candidate.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = candidate.AbsolutePath.Trim('/').Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 1
                && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in candidate.Query.TrimStart('?').Split('&'))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length == 2
                        && pair[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                    {
                        // YouTube IDs use only unreserved ASCII, so accepting escaped query
                        // material adds ambiguity and can make malformed percent escapes throw.
                        videoId = pair[1];
                        break;
                    }
                }
            }
            else if (segments.Length == 2
                     && (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                         || segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                         || segments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
            {
                videoId = segments[1];
            }
        }

        if (!IsYoutubeVideoId(videoId)) return false;
        uri = new Uri($"https://www.youtube.com/watch?v={videoId}");
        return true;
    }

    private static bool IsValidKey(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1) return false;
        var provider = value[..separator];
        var id = value[(separator + 1)..];
        return (provider.Equals("tvdb", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("tmdb", StringComparison.OrdinalIgnoreCase))
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            && numericId > 0;
    }

    private static bool IsYoutubeVideoId(string? value)
        => value is { Length: 11 }
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-');

    private static async Task WriteEmptyFileAtomicAsync(string path, CancellationToken ct)
    {
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new Dictionary<string, string>(),
                    JsonOptions,
                    ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
