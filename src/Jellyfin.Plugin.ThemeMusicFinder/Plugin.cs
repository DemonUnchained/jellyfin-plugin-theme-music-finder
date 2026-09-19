using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class ThemeMusicFinderPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public ThemeMusicFinderPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static ThemeMusicFinderPlugin? Instance { get; private set; }

    public override string Name => "Theme Music Finder";

    public override Guid Id => Guid.Parse("03459bac-6165-4b2d-b05f-40b953159b59");

    public override string Description =>
        "Finds TV theme songs through ThemerrDB by TMDB ID, library-aware strictly matched AnimeThemes openings or safe ending fallback, "
        + "then Plex by TVDB ID, "
        + "and saves them as theme.mp3 in each series folder, "
        + "so Jellyfin can play them on series pages. Requires write access to your TV "
        + "library. Never overwrites an existing theme.mp3.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.configPage.html"
        }
    ];
}
