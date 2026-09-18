using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class ThemeMusicFinderScheduledTask(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IApplicationPaths appPaths,
    IFileSystem fileSystem,
    IMediaEncoder mediaEncoder,
    ILoggerFactory loggerFactory) : IScheduledTask
{
    public string Name => "Find missing theme music";
    public string Key => "ThemeMusicFinderDownload";
    public string Description => "Finds missing TV series themes through ThemerrDB, AnimeThemes, and Plex, then saves normalized theme.mp3 files.";
    public string Category => "Theme Music Finder";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Shared with ItemAddedListener: both touch the same ThemeMusicFinder.attempts.json file, and
        // holding this for the whole sweep (which can run several minutes) is intended - item-added
        // work is background and non-urgent, so it queues behind the sweep rather than racing it.
        await ThemeMusicFinderGate.AttemptsFile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var plexHttp = PlexThemeProvider.CreateClient();
            using var themerrHttp = ThemerrThemeProvider.CreateClient();
            using var animeThemesHttp = AnimeThemesProvider.CreateClient();
            var config = ThemeMusicFinderPlugin.Instance?.Configuration ?? new PluginConfiguration();
            var audioProcessor = new FfmpegThemeAudioProcessor(
                appPaths,
                mediaEncoder,
                config.EnableLoudnessNormalization,
                config.NormalizationTargetLufs);

            var providers = new List<IThemeProvider>();
            if (config.EnableThemerrFallback)
            {
                providers.Add(new ThemerrThemeProvider(
                    themerrHttp,
                    new YoutubeThemeAudioDownloader(appPaths, mediaEncoder, audioProcessor)));
            }

            if (config.EnableAnimeThemes)
                providers.Add(new AnimeThemesProvider(animeThemesHttp, audioProcessor));
            providers.Add(new PlexThemeProvider(
                plexHttp,
                config.EnableLoudnessNormalization ? audioProcessor : null));
            IThemeProvider provider = new CompositeThemeProvider([.. providers]);

            var store = new AttemptStore(
                Path.Combine(appPaths.PluginConfigurationsPath, "ThemeMusicFinder.attempts.json"),
                loggerFactory.CreateLogger<AttemptStore>());
            var service = new ThemeDownloadService(
                libraryManager, providerManager, provider, store, fileSystem,
                loggerFactory.CreateLogger<ThemeDownloadService>());

            var written = await service.RunAsync(progress, cancellationToken).ConfigureAwait(false);
            loggerFactory.CreateLogger<ThemeMusicFinderScheduledTask>()
                .LogInformation("Theme Music Finder task finished: {Written} theme(s) downloaded.", written);
        }
        finally
        {
            ThemeMusicFinderGate.AttemptsFile.Release();
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];
}
