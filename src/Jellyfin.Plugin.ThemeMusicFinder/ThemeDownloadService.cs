using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

public class ThemeDownloadService(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IThemeProvider themeProvider,
    AttemptStore attempts,
    IFileSystem fileSystem,
    ILogger<ThemeDownloadService> logger)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    /// <summary>The wait between upstream requests. Production leaves this as
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; the tests substitute a recorder so
    /// they can assert the pacing — how many waits happen, how long each one is, and which
    /// outcomes pay for one — without actually sleeping through 305 of them. The default is the
    /// real delay, so nothing about the shipped behaviour depends on a test having set it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>One instance is constructed per run (nightly sweep or single item-added
    /// series), so this gives "log the write failure once per run, not once per item" without
    /// the two trigger paths having to agree on it separately.</summary>
    private bool _writeFailureLogged;

    public async Task<int> RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await attempts.LoadAsync(ct).ConfigureAwait(false);

        var series = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Series>().ToList();

        var written = 0;

        try
        {
            for (var i = 0; i < series.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(i * 100.0 / series.Count);

                Outcome result;
                try
                {
                    result = await TryOneAsync(series[i], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Real cancellation of THIS run must still stop the sweep. Without the
                    // filter, a TaskCanceledException from an HttpClient timeout (which derives
                    // from OperationCanceledException) would be misclassified as cancellation
                    // and abort the run instead of falling through to the catch below.
                    throw;
                }
                catch (Exception ex)
                {
                    // One sick series (e.g. a refresh failure on locked/corrupt metadata,
                    // or an HttpClient timeout) must not take down the rest of the sweep.
                    logger.LogWarning(ex, "Unexpected error processing {Series}", series[i].Name);
                    continue;
                }

                if (result == Outcome.Written) written++;
                if (i < series.Count - 1 && result != Outcome.Skipped) await DelayAsync(Throttle, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Save even if the run was cancelled or a series threw past the catch above,
            // so recorded failures aren't lost and the backoff is honoured next run.
            // Use CancellationToken.None: if ct is already cancelled, the save must still succeed.
            // Guarded because an exception out of a finally block *replaces* whatever was
            // propagating — a disk-full here would masquerade as the cause of a clean shutdown.
            try
            {
                await attempts.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not persist attempt history; backoff may be re-tried next run.");
            }
        }

        progress?.Report(100);
        logger.LogInformation("Theme sweep complete: {Written} themes written.", written);
        return written;
    }

    /// <summary>Runs a single series through the same fetch/write/refresh pipeline as the nightly
    /// sweep. Returns the full outcome rather than a bool: callers need
    /// <c>!= <see cref="Outcome.Skipped"/></c> to decide whether to pay the upstream throttle
    /// (a series that was skipped - no catalogue id, theme already present, inside the backoff
    /// window - made no request and must not pay one), but they also need to be able to see
    /// <see cref="Outcome.Unwritable"/>, which collapsing to a bool discards entirely.</summary>
    public async Task<Outcome> RunForSeriesAsync(Series series, CancellationToken ct)
        => await TryOneAsync(series, ct).ConfigureAwait(false);

    public enum Outcome { Skipped, Written, Fetched, Unwritable }

    private async Task<Outcome> TryOneAsync(Series series, CancellationToken ct)
    {
        var tvdbId = series.GetProviderId(MetadataProvider.Tvdb);
        var tmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        // No stable catalogue id is normal (e.g. YouTube channel "series") — not an error.
        if ((string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId))
            || string.IsNullOrEmpty(series.Path)) return Outcome.Skipped;

        var dest = Path.Combine(series.Path, "theme.mp3");
        if (File.Exists(dest)) return Outcome.Skipped;

        var config = ThemeMusicFinderPlugin.Instance?.Configuration ?? new PluginConfiguration();
        // Belt and braces alongside the clamp in PluginConfiguration: a 0 here disables backoff.
        var retryAfterDays = Math.Max(1, config.RetryAfterDays);
        // Preserve existing TVDB-based history after upgrading. TMDB-only series get a
        // namespaced key so identifiers from the two catalogues can never collide.
        var attemptKey = !string.IsNullOrEmpty(tvdbId) ? tvdbId : $"tmdb:{tmdbId}";
        if (!attempts.ShouldTry(attemptKey, retryAfterDays, DateTimeOffset.UtcNow)) return Outcome.Skipped;

        ThemeFetchResult fetch;
        try
        {
            fetch = await themeProvider.FetchAsync(series, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Transient: do NOT record a failure, so it retries on the next run.
            logger.LogWarning(ex, "Theme fetch failed for {Series}", series.Name);
            return Outcome.Fetched;
        }

        if (fetch.Status == ThemeFetchStatus.NotFound)
        {
            // The only case that earns a backoff. Information, not Debug: Jellyfin does not emit
            // Debug at its default level, and "why did nothing happen?" is the question a user
            // actually has when the plugin appears idle.
            attempts.RecordFailure(attemptKey, DateTimeOffset.UtcNow);
            logger.LogInformation(
                "No theme available for {Series} (tvdb {Tvdb}, tmdb {Tmdb}): {Reason}. Will not ask again for {Days} day(s).",
                series.Name, tvdbId ?? "none", tmdbId ?? "none", fetch.Reason, retryAfterDays);
            return Outcome.Fetched;
        }

        if (fetch.Status == ThemeFetchStatus.Transient)
        {
            // Deliberately NOT recorded. A bad hour upstream must not mark the whole library
            // themeless for the length of the backoff window.
            logger.LogWarning(
                "Theme lookup for {Series} (tvdb {Tvdb}, tmdb {Tmdb}) did not succeed: {Reason}. Not recording a failure; will retry on the next run.",
                series.Name, tvdbId ?? "none", tmdbId ?? "none", fetch.Reason);
            return Outcome.Fetched;
        }

        if (fetch.Status == ThemeFetchStatus.NotApplicable)
        {
            return Outcome.Skipped;
        }

        var body = fetch.Body!;

        try
        {
            await ThemeFile.WriteAtomicAsync(dest, body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!_writeFailureLogged)
            {
                // Once per run, not per item: 305 identical errors buries the one that matters.
                // The exception and the path are the whole point - "library is not writable" is
                // actively misleading for a full disk or a refused overwrite.
                _writeFailureLogged = true;
                logger.LogError(
                    ex,
                    "Cannot write theme for {Series} to {Path}; no further write errors will be logged this run.",
                    series.Name, dest);
            }

            return Outcome.Unwritable;
        }

        // Jellyfin does not expose ThemeMedia until the item is refreshed — verified.
        await providerManager.RefreshSingleItem(
            series,
            new MetadataRefreshOptions(new DirectoryService(fileSystem)),
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "Saved theme for {Series} from {Source} (tvdb {Tvdb}, tmdb {Tmdb}; source {SourceUri})",
            series.Name,
            fetch.Source ?? "unknown provider",
            tvdbId ?? "none",
            tmdbId ?? "none",
            fetch.SourceUri?.ToString() ?? "not reported");
        return Outcome.Written;
    }
}
