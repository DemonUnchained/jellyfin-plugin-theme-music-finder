using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class PluginConfiguration : BasePluginConfiguration
{
    private int _retryAfterDays = DefaultRetryAfterDays;

    private const int DefaultRetryAfterDays = 7;

    /// <summary>Days to wait before retrying a show whose theme was not found.
    /// Clamped to at least 1: the config page's parseInt('') yields NaN, which serialises to
    /// null and deserialises into this int as 0, and 0 disables backoff entirely — every
    /// missing show would be re-requested against the free upstream every single night.
    /// The clamp lives here rather than only in the browser because the plugin configuration
    /// API can be called directly.</summary>
    public int RetryAfterDays
    {
        get => _retryAfterDays;
        set => _retryAfterDays = Math.Max(1, value);
    }

    /// <summary>Fetch a theme as soon as a new series is added.</summary>
    public bool EnableItemAddedHook { get; set; } = true;

    /// <summary>Use ThemerrDB's curated TMDB-to-YouTube mapping as the preferred source.
    /// The legacy property name is retained so existing installations keep their setting.</summary>
    public bool EnableThemerrFallback { get; set; } = true;

    /// <summary>After a ThemerrDB miss, try a strictly matched AnimeThemes opening.</summary>
    public bool EnableAnimeThemes { get; set; } = true;

    /// <summary>After exact-ID sources miss, autonomously search and score multiple theme candidates.</summary>
    public bool EnableAutomaticDiscovery { get; set; } = true;

    /// <summary>Explicit opt-in for metadata-selected third-party sources to write theme.mp3.
    /// False by default until a read-only whole-library audit has been reviewed. Existing
    /// trusted ID-matched provider behavior is unchanged.</summary>
    public bool EnableAutomaticDiscoveryWrites { get; set; } = false;

    /// <summary>Search SoundCloud for additional high-confidence theme recordings.</summary>
    public bool EnableSoundCloudDiscovery { get; set; } = true;

    /// <summary>Search Bilibili only for series identified as animation/anime.</summary>
    public bool EnableBilibiliDiscovery { get; set; } = true;

    /// <summary>Normalize new downloads with FFmpeg so themes play at a consistent volume.</summary>
    public bool EnableLoudnessNormalization { get; set; } = true;

    private double _normalizationTargetLufs = -18.0;

    /// <summary>EBU R128 integrated loudness target, constrained to a safe range.</summary>
    public double NormalizationTargetLufs
    {
        get => _normalizationTargetLufs;
        set => _normalizationTargetLufs = Math.Clamp(value, -24.0, -12.0);
    }
}
