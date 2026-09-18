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
    public string Description => "Finds missing TV series themes through Plex and the ThemerrDB fallback, then saves them as theme.mp3.";
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

            IThemeProvider provider = new PlexThemeProvider(plexHttp);
            if (ThemeMusicFinderPlugin.Instance?.Configuration.EnableThemerrFallback != false)
            {
                provider = new CompositeThemeProvider(
                    provider,
                    new ThemerrThemeProvider(
                        themerrHttp,
                        new YoutubeThemeAudioDownloader(appPaths, mediaEncoder)));
            }

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
