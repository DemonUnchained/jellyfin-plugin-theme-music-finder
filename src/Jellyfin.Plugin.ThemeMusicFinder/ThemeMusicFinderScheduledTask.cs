using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Entities.TV;
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
    public string Description => "Finds missing TV series themes through curated stable-ID overrides, ThemerrDB, AnimeThemes, and Plex, then saves normalized theme.mp3 files and an unresolved-series report.";
    public string Category => "Theme Music Finder";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Shared with ItemAddedListener: both touch the same attempt-history file, and
        // holding this for the whole sweep (which can run several minutes) is intended - item-added
        // work is background and non-urgent, so it queues behind the sweep rather than racing it.
        await ThemeMusicFinderGate.AttemptsFile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var taskLogger = loggerFactory.CreateLogger<ThemeMusicFinderScheduledTask>();
            using var plexHttp = PlexThemeProvider.CreateClient();
            using var themerrHttp = ThemerrThemeProvider.CreateClient();
            using var animeThemesHttp = AnimeThemesProvider.CreateClient();
            var config = ThemeMusicFinderPlugin.Instance?.Configuration ?? new PluginConfiguration();
            var audioProcessor = new FfmpegThemeAudioProcessor(
                appPaths,
                mediaEncoder,
                config.EnableLoudnessNormalization,
                config.NormalizationTargetLufs);
            var youtubeDownloader = new YoutubeThemeAudioDownloader(
                appPaths,
                mediaEncoder,
                audioProcessor);
            var sourceOverrides = await CuratedThemeSourceStore.LoadOrCreateAsync(
                Path.Combine(
                    appPaths.PluginConfigurationsPath,
                    "ThemeMusicFinder.source-overrides.json"),
                taskLogger,
                cancellationToken).ConfigureAwait(false);

            var providers = new List<IThemeProvider>();
            var measuredProviders = new List<MeasuredThemeProvider>();
            AnimeThemesProvider? animeThemesProvider = null;
            AnimeLibraryThemeProvider? animeLibraryProvider = null;
            AnimeThemeTitleOverrideStore? animeTitleOverrides = null;

            void AddProvider(string name, IThemeProvider provider)
            {
                var measured = new MeasuredThemeProvider(name, provider);
                measuredProviders.Add(measured);
                providers.Add(measured);
            }

            if (sourceOverrides.Mappings.Count > 0)
            {
                AddProvider(
                    "Curated source overrides",
                    new CuratedThemeSourceProvider(sourceOverrides, youtubeDownloader));
            }

            if (config.EnableThemerrFallback)
            {
                AddProvider("ThemerrDB", new ThemerrThemeProvider(
                    themerrHttp,
                    youtubeDownloader));
            }

            if (config.EnableAnimeThemes)
            {
                animeTitleOverrides = await AnimeThemeTitleOverrideStore.LoadOrCreateAsync(
                    Path.Combine(
                        appPaths.PluginConfigurationsPath,
                        "ThemeMusicFinder.animethemes-overrides.json"),
                    taskLogger,
                    cancellationToken).ConfigureAwait(false);
                animeThemesProvider = new AnimeThemesProvider(
                    animeThemesHttp,
                    audioProcessor,
                    animeTitleOverrides.Mappings);
                animeLibraryProvider = new AnimeLibraryThemeProvider(libraryManager, animeThemesProvider);
                AddProvider("AnimeThemes", animeLibraryProvider);
            }

            AddProvider("Plex", new PlexThemeProvider(
                plexHttp,
                config.EnableLoudnessNormalization ? audioProcessor : null));
            IThemeProvider provider = new CompositeThemeProvider([.. providers]);

            var store = new AttemptStore(
                Path.Combine(appPaths.PluginConfigurationsPath, "ThemeMusicFinder.attempts-v6.json"),
                loggerFactory.CreateLogger<AttemptStore>(),
                // Prefer v1.2.1.6 history. A direct v1.2.1.5 -> v1.2.1.7 upgrade falls back to
                // v4 and invalidates only the three aliases verified to be genuine anime.
                legacyPath: Path.Combine(
                    appPaths.PluginConfigurationsPath,
                    "ThemeMusicFinder.attempts-v5.json"),
                fallbackLegacyPath: Path.Combine(
                    appPaths.PluginConfigurationsPath,
                    "ThemeMusicFinder.attempts-v4.json"),
                fallbackExcludedLegacyKeys: AnimeThemesProvider.CorrectedAttemptKeys,
                legacyKeyRewrites: AnimeThemesProvider.RevokedAttemptKeyRewrites);
            var service = new ThemeDownloadService(
                libraryManager, providerManager, provider, store, fileSystem,
                loggerFactory.CreateLogger<ThemeDownloadService>(),
                Path.Combine(appPaths.PluginConfigurationsPath, "ThemeMusicFinder.missing-themes.json"),
                sourceOverrides.Mappings.Count == 0 && animeTitleOverrides is null
                    ? null
                    : new Func<Series, string?>(series =>
                        AttemptKeySuffix.Combine(
                            sourceOverrides.GetCacheSuffix(series),
                            animeTitleOverrides is null
                                ? null
                                : AnimeThemesProvider.GetOverrideCacheSuffix(
                                    series,
                                    animeTitleOverrides.Mappings))));

            var written = await service.RunAsync(progress, cancellationToken).ConfigureAwait(false);
            foreach (var measured in measuredProviders) measured.Log(taskLogger);
            if (animeThemesProvider is not null && animeLibraryProvider is not null)
            {
                taskLogger.LogInformation(
                    "AnimeThemes HTTP activity: {Searches} search request(s), {Normalized} normalized-query request(s), {Details} detail request(s), {Audio} audio download(s); {Skipped} known non-anime series skipped before HTTP.",
                    animeThemesProvider.SearchRequestCount,
                    animeThemesProvider.NormalizedQueryRequestCount,
                    animeThemesProvider.DetailRequestCount,
                    animeThemesProvider.AudioRequestCount,
                    animeLibraryProvider.SkippedSeriesCount);
            }

            taskLogger.LogInformation(
                "Theme Music Finder task finished: {Written} theme(s) downloaded.",
                written);
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
