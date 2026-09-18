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

    /// <summary>After a definitive Plex miss, try ThemerrDB's curated TMDB-to-YouTube mapping.</summary>
    public bool EnableThemerrFallback { get; set; } = true;
}
