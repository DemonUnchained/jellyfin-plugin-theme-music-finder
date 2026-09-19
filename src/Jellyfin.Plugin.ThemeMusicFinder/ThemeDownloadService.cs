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
    ILogger<ThemeDownloadService> logger,
    string? reportPath = null)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    /// <summary>The wait between upstream requests. Production leaves this as
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; the tests substitute a recorder so
    /// they can assert the pacing — how many waits happen, how long each one is, and which
    /// outcomes pay for one — without actually sleeping through hundreds of them.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>One instance is constructed per run (nightly sweep or single item-added
    /// series), so this gives "log the write failure once per run, not once per item" without
    /// the two trigger paths having to agree on it separately.</summary>
    private bool _writeFailureLogged;

    /// <summary>Provider-wide outages can affect hundreds of series in one sweep. Keep every
    /// outcome in the final counters, but emit one actionable warning per distinct reason and
    /// send repetitions to Debug so Jellyfin's normal log is not buried.</summary>
    private readonly HashSet<string> _loggedTransientReasons = new(StringComparer.Ordinal);

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
        var skipped = 0;
        var notFound = 0;
        var transient = 0;
        var unwritable = 0;
        var unexpected = 0;
        var skipCounts = Enum.GetValues<SkipReason>().ToDictionary(reason => reason, _ => 0);
        var reportEntries = new List<MissingThemeReportEntry>();

        try
        {
            for (var i = 0; i < series.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(i * 100.0 / series.Count);

                ProcessResult result;
                try
                {
                    result = await TryOneAsync(series[i], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Real cancellation of THIS run must still stop the sweep. Without the
                    // filter, a TaskCanceledException from an HttpClient timeout (which derives
                    // from OperationCanceledException) would be misclassified as cancellation.
                    throw;
                }
                catch (Exception ex)
                {
                    // One sick series (e.g. a refresh failure on locked/corrupt metadata,
                    // or an HttpClient timeout) must not take down the rest of the sweep.
                    logger.LogWarning(ex, "Unexpected error processing {Series}", series[i].Name);
                    unexpected++;
                    reportEntries.Add(CreateReportEntry(
                        series[i],
                        "unexpected-error",
                        ex.Message));
                    continue;
                }

                switch (result.Outcome)
                {
                    case Outcome.Skipped:
                        skipped++;
                        skipCounts[result.SkipReason
                            ?? throw new InvalidOperationException("A skipped result must name its reason.")]++;
                        break;
                    case Outcome.Written: written++; break;
                    case Outcome.NotFound: notFound++; break;
                    case Outcome.Transient: transient++; break;
                    case Outcome.Unwritable: unwritable++; break;
                    default: throw new InvalidOperationException($"Unknown theme outcome {result.Outcome}.");
                }

                if (ShouldReport(result))
                {
                    reportEntries.Add(CreateReportEntry(
                        series[i],
                        GetReportStatus(result),
                        result.Reason ?? GetDefaultReason(result)));
                }

                if (i < series.Count - 1 && result.Outcome != Outcome.Skipped)
                    await DelayAsync(Throttle, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Save even if the run was cancelled or a series threw past the catch above,
            // so recorded failures aren't lost and the backoff is honoured next run.
            // Use CancellationToken.None: if ct is already cancelled, the save must still succeed.
            // Guarded because an exception out of a finally block replaces the original fault.
            try
            {
                await attempts.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not persist attempt history; backoff may be re-tried next run.");
            }
        }

        if (reportPath is not null)
        {
            try
            {
                await MissingThemeReport.WriteAtomicAsync(
                    reportPath,
                    new MissingThemeReport
                    {
                        GeneratedAtUtc = DateTimeOffset.UtcNow,
                        TotalSeriesScanned = series.Count,
                        Entries = reportEntries
                    },
                    ct).ConfigureAwait(false);
                logger.LogInformation(
                    "Wrote missing-theme report with {Count} unresolved series to {Path}.",
                    reportEntries.Count,
                    reportPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write the missing-theme report to {Path}.", reportPath);
            }
        }

        progress?.Report(100);
        logger.LogInformation(
            "Theme sweep complete: {Written} written, {NotFound} not found, {Transient} transient failure(s), {Unwritable} write failure(s), {Unexpected} unexpected error(s), {Skipped} skipped ({ExistingTheme} existing theme, {Backoff} in backoff, {NoStableId} no stable ID, {MissingPath} missing path, {NotApplicable} provider not applicable); {Scanned} total series scanned.",
            written,
            notFound,
            transient,
            unwritable,
            unexpected,
            skipped,
            skipCounts[SkipReason.ExistingTheme],
            skipCounts[SkipReason.Backoff],
            skipCounts[SkipReason.NoStableId],
            skipCounts[SkipReason.MissingPath],
            skipCounts[SkipReason.ProviderNotApplicable],
            series.Count);
        return written;
    }

    /// <summary>Runs a single series through the same fetch/write/refresh pipeline as the nightly
    /// sweep. Returns the full outcome rather than a bool so the item-added path can distinguish
    /// an upstream request from a local skip and can surface an unwritable library.</summary>
    public async Task<Outcome> RunForSeriesAsync(Series series, CancellationToken ct)
        => (await TryOneAsync(series, ct).ConfigureAwait(false)).Outcome;

    public enum Outcome { Skipped, Written, NotFound, Transient, Unwritable }

    private enum SkipReason { ExistingTheme, Backoff, NoStableId, MissingPath, ProviderNotApplicable }

    private sealed record ProcessResult(
        Outcome Outcome,
        SkipReason? SkipReason = null,
        string? Reason = null);

    private async Task<ProcessResult> TryOneAsync(Series series, CancellationToken ct)
    {
        var tvdbId = series.GetProviderId(MetadataProvider.Tvdb);
        var tmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        // No stable catalogue id is normal (e.g. YouTube channel "series") — not an error.
        if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId))
            return new ProcessResult(Outcome.Skipped, SkipReason.NoStableId);
        if (string.IsNullOrEmpty(series.Path))
            return new ProcessResult(Outcome.Skipped, SkipReason.MissingPath);

        var dest = Path.Combine(series.Path, "theme.mp3");
        if (File.Exists(dest))
            return new ProcessResult(Outcome.Skipped, SkipReason.ExistingTheme);

        var config = ThemeMusicFinderPlugin.Instance?.Configuration ?? new PluginConfiguration();
        // Belt and braces alongside the clamp in PluginConfiguration: a 0 here disables backoff.
        var retryAfterDays = Math.Max(1, config.RetryAfterDays);
        // TVDB-based history remains unnamespaced. TMDB-only series use a namespaced key so
        // identifiers from the two catalogues can never collide.
        var attemptKey = !string.IsNullOrEmpty(tvdbId) ? tvdbId : $"tmdb:{tmdbId}";
        if (!attempts.ShouldTry(attemptKey, retryAfterDays, DateTimeOffset.UtcNow))
        {
            return new ProcessResult(
                Outcome.Skipped,
                SkipReason.Backoff,
                $"A confirmed miss is inside the {retryAfterDays}-day retry window.");
        }

        ThemeFetchResult fetch;
        try
        {
            fetch = await themeProvider.FetchAsync(series, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Transient: do NOT record a failure, so it retries on the next run.
            var reason = $"HTTP request failed: {ex.Message}";
            LogTransientOnce(series, tvdbId, tmdbId, reason, ex);
            return new ProcessResult(Outcome.Transient, Reason: reason);
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
            return new ProcessResult(Outcome.NotFound, Reason: fetch.Reason);
        }

        if (fetch.Status is ThemeFetchStatus.Transient or ThemeFetchStatus.CandidateUnavailable)
        {
            // Deliberately NOT recorded. A bad hour upstream must not mark the whole library
            // themeless for the length of the backoff window.
            var reason = fetch.Reason ?? "transient provider failure";
            LogTransientOnce(series, tvdbId, tmdbId, reason);
            return new ProcessResult(Outcome.Transient, Reason: reason);
        }

        if (fetch.Status == ThemeFetchStatus.NotApplicable)
        {
            return new ProcessResult(
                Outcome.Skipped,
                SkipReason.ProviderNotApplicable,
                fetch.Reason);
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
                // Once per run, not per item: hundreds of identical errors bury the useful one.
                _writeFailureLogged = true;
                logger.LogError(
                    ex,
                    "Cannot write theme for {Series} to {Path}; no further write errors will be logged this run.",
                    series.Name, dest);
            }

            return new ProcessResult(Outcome.Unwritable, Reason: ex.Message);
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
        return new ProcessResult(Outcome.Written);
    }

    private static bool ShouldReport(ProcessResult result)
        => result.Outcome is not Outcome.Written
            && result.SkipReason is not SkipReason.ExistingTheme;

    private static string GetReportStatus(ProcessResult result)
        => result.Outcome switch
        {
            Outcome.NotFound => "not-found",
            Outcome.Transient => "transient",
            Outcome.Unwritable => "unwritable",
            Outcome.Skipped when result.SkipReason == SkipReason.Backoff => "backoff",
            Outcome.Skipped when result.SkipReason == SkipReason.NoStableId => "no-stable-id",
            Outcome.Skipped when result.SkipReason == SkipReason.MissingPath => "missing-path",
            Outcome.Skipped when result.SkipReason == SkipReason.ProviderNotApplicable => "not-applicable",
            _ => throw new InvalidOperationException($"Outcome {result.Outcome} is not reportable.")
        };

    private static string GetDefaultReason(ProcessResult result)
        => result.SkipReason switch
        {
            SkipReason.Backoff => "A confirmed miss is still inside the retry window.",
            SkipReason.NoStableId => "The series has neither a TVDB nor a TMDB identifier.",
            SkipReason.MissingPath => "The series has no physical folder path.",
            SkipReason.ProviderNotApplicable => "No enabled provider could look up this series.",
            _ => result.Outcome switch
            {
                Outcome.NotFound => "No provider found a theme.",
                Outcome.Transient => "The lookup failed temporarily and will retry.",
                Outcome.Unwritable => "The theme could not be written to the series folder.",
                _ => "Unresolved."
            }
        };

    private static MissingThemeReportEntry CreateReportEntry(
        Series series,
        string status,
        string reason)
        => new(
            series.Name,
            string.IsNullOrWhiteSpace(series.OriginalTitle) ? null : series.OriginalTitle,
            series.ProductionYear,
            series.GetProviderId(MetadataProvider.Tvdb),
            series.GetProviderId(MetadataProvider.Tmdb),
            string.IsNullOrWhiteSpace(series.Path) ? null : series.Path,
            status,
            reason);

    private void LogTransientOnce(
        Series series,
        string? tvdbId,
        string? tmdbId,
        string reason,
        Exception? exception = null)
    {
        if (_loggedTransientReasons.Add(reason))
        {
            logger.LogWarning(
                exception,
                "Theme lookup for {Series} (tvdb {Tvdb}, tmdb {Tmdb}) did not succeed: {Reason}. Not recording a failure; later providers were attempted and the series will retry on the next run. Further identical failures will only appear at Debug level this run.",
                series.Name,
                tvdbId ?? "none",
                tmdbId ?? "none",
                reason);
            return;
        }

        logger.LogDebug(
            exception,
            "Repeated transient theme lookup failure for {Series} (tvdb {Tvdb}, tmdb {Tmdb}): {Reason}",
            series.Name,
            tvdbId ?? "none",
            tmdbId ?? "none",
            reason);
    }
}
