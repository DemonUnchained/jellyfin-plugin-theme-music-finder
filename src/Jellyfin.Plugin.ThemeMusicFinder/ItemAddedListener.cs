using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class ItemAddedListener(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IApplicationPaths appPaths,
    IFileSystem fileSystem,
    IMediaEncoder mediaEncoder,
    ILoggerFactory loggerFactory) : IHostedService
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    private readonly ILogger<ItemAddedListener> _logger = loggerFactory.CreateLogger<ItemAddedListener>();
    private readonly CancellationTokenSource _cts = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemAdded;
        // Cancels every in-flight handler body below, not just future ones: unsubscribing alone
        // only stops new work, but an already-running Task.Run could still be mid-flight against
        // libraryManager/providerManager while the host is tearing them down.
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (ThemeMusicFinderPlugin.Instance?.Configuration.EnableItemAddedHook != true) return;
        if (e.Item is not Series series) return;

        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            // Shared with ThemeMusicFinderScheduledTask: both touch the same attempt-history
            // file, and this queues behind a running nightly sweep rather than racing it.
            var gateAcquired = false;
            try
            {
                await ThemeMusicFinderGate.AttemptsFile.WaitAsync(ct).ConfigureAwait(false);
                gateAcquired = true;

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
                    Path.Combine(appPaths.PluginConfigurationsPath, "ThemeMusicFinder.attempts-v4.json"),
                    loggerFactory.CreateLogger<AttemptStore>());
                await store.LoadAsync(ct).ConfigureAwait(false);

                var service = new ThemeDownloadService(
                    libraryManager, providerManager, provider, store, fileSystem,
                    loggerFactory.CreateLogger<ThemeDownloadService>());

                var outcome = ThemeDownloadService.Outcome.Skipped;
                try
                {
                    outcome = await service.RunForSeriesAsync(series, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Save even if RunForSeriesAsync was cancelled mid-flight, so a recorded
                    // failure isn't lost. CancellationToken.None deliberately: a cancelled token
                    // here would lose the very records this save exists to protect (the same
                    // reasoning behind ThemeDownloadService.RunAsync's own finally-save).
                    // Guarded: an exception out of a finally replaces the one propagating, which
                    // would turn a clean cancellation into a bogus I/O fault.
                    try
                    {
                        await store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogWarning(saveEx, "Could not persist attempt history after handling {Series}", series.Name);
                    }
                }

                if (outcome == ThemeDownloadService.Outcome.Unwritable)
                {
                    // The nightly sweep surfaces this; this path used to swallow it, so a
                    // read-only library silently ate every newly added series.
                    _logger.LogError(
                        "Could not save a theme for the newly added series {Series} - see the preceding write error for the path and cause.",
                        series.Name);
                }

                // A series that was skipped (no catalogue id, theme already present, inside the
                // backoff window) made no upstream request and must not pay the throttle.
                if (outcome != ThemeDownloadService.Outcome.Skipped)
                {
                    // ~1/sec throttle upstream, matching the nightly sweep's throttle. Only paid
                    // when an upstream request actually happened - a scan that bulk-adds series
                    // whose themes are already on disk (or outside the retry window) must not
                    // stall behind this.
                    await Task.Delay(Throttle, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal on shutdown (StopAsync cancelled the token) - not a fault, don't warn.
            }
            catch (Exception ex)
            {
                // RunForSeriesAsync has no per-series exception guard (unlike the nightly RunAsync's
                // sweep loop), so this catch is the only thing standing between a fault here - a
                // network error, a write failure, a refresh failure - and total silence.
                _logger.LogWarning(ex, "Item-added theme fetch failed for {Series}", series.Name);
            }
            finally
            {
                if (gateAcquired) ThemeMusicFinderGate.AttemptsFile.Release();
            }
        });
    }
}
