using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>
/// Read-only, on-demand whole-library validation of the autonomous matching rules.
/// It calls the same search, shortlisting and full-metadata validation pipeline as the
/// downloader, but must NEVER download audio, create theme.mp3 or mutate attempt history.
/// </summary>
public sealed class ThemeDiscoveryAuditScheduledTask(
    ILibraryManager libraryManager,
    IApplicationPaths appPaths,
    ILoggerFactory loggerFactory) : IScheduledTask
{
    public string Name => "Audit automatic theme discovery (read only)";
    public string Key => "ThemeMusicFinderAudit";
    public string Description =>
        "Evaluates YouTube, SoundCloud, and anime Bilibili candidates for every TV show missing "
        + "theme.mp3. Writes ThemeMusicFinder.discovery-audit.json without downloading music "
        + "or changing retry history. Run manually before enabling automatic theme writes.";
    public string Category => "Theme Music Finder";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Share the gate with the existing nightly task and item-added hook so an audit
        // cannot stack external requests on top of an existing potentially long sweep.
        await ThemeMusicFinderGate.AttemptsFile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var log = loggerFactory.CreateLogger<ThemeDiscoveryAuditScheduledTask>();
            var config = ThemeMusicFinderPlugin.Instance?.Configuration ?? new PluginConfiguration();
            var neverDownload = new RejectAuditDownloader();

            // This instance retains source outage circuit-breaker state throughout the sweep.
            // It is the actual production discovery engine, configured in preview-only mode.
            var discovery = new AutonomousThemeDiscoveryProvider(
                appPaths,
                neverDownload,
                libraryManager,
                config.EnableSoundCloudDiscovery,
                config.EnableBilibiliDiscovery,
                auditOnly: true);

            var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false
            }).OfType<Series>().ToList();

            var output = new List<ThemeDiscoveryAuditEntry>();
            var skippedExisting = 0;
            var noPath = 0;
            var chosen = 0;
            var rejected = 0;
            var transient = 0;
            var unavailable = 0;

            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var series = items[i];
                progress?.Report(items.Count == 0 ? 100 : i * 100.0 / items.Count);

                if (string.IsNullOrWhiteSpace(series.Path))
                {
                    noPath++;
                    continue;
                }
                if (File.Exists(Path.Combine(series.Path, "theme.mp3")))
                {
                    skippedExisting++;
                    continue;
                }

                ThemeFetchResult result;
                try
                {
                    result = await discovery.FetchAsync(series, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Discovery audit failed for {Series}", series.Name);
                    result = ThemeFetchResult.Transient("unexpected audit error: " + ex.Message);
                }

                string status;
                switch (result.Status)
                {
                    case ThemeFetchStatus.Preview:
                        chosen++;
                        status = "candidate";
                        break;
                    case ThemeFetchStatus.NotFound:
                        rejected++;
                        status = "rejected";
                        break;
                    case ThemeFetchStatus.Transient:
                        transient++;
                        status = "transient";
                        break;
                    case ThemeFetchStatus.NotApplicable:
                        unavailable++;
                        status = "not-applicable";
                        break;
                    case ThemeFetchStatus.CandidateUnavailable:
                        transient++;
                        status = "candidate-unavailable";
                        break;
                    default:
                        // No audio can possibly be produced in audit mode. If a future
                        // refactor changes this invariant, fail loudly without touching media.
                        throw new InvalidOperationException(
                            "Read-only audit received an unexpected downloaded-audio result.");
                }

                output.Add(new ThemeDiscoveryAuditEntry(
                    series.Name,
                    series.OriginalTitle,
                    series.ProductionYear,
                    series.GetProviderId(MetadataProvider.Tvdb),
                    series.GetProviderId(MetadataProvider.Tmdb),
                    series.Path,
                    status,
                    result.Reason ?? "no reason supplied",
                    result.Source,
                    result.SourceUri?.ToString(),
                    result.CandidateTitle,
                    result.ConfidenceScore));
            }

            // Only the report file is written. Existing themes, catalog overrides and the
            // attempt/backoff cache are never opened for writing by this scheduled task.
            var path = Path.Combine(appPaths.PluginConfigurationsPath,
                "ThemeMusicFinder.discovery-audit.json");
            await ThemeDiscoveryAuditReport.WriteAtomicAsync(
                path,
                new ThemeDiscoveryAuditReport
                {
                    GeneratedAtUtc = DateTimeOffset.UtcNow,
                    TotalSeriesScanned = items.Count,
                    ExistingThemesSkipped = skippedExisting,
                    MissingPathsSkipped = noPath,
                    CandidateCount = chosen,
                    RejectedCount = rejected,
                    TransientCount = transient,
                    NotApplicableCount = unavailable,
                    Entries = output
                },
                cancellationToken).ConfigureAwait(false);

            progress?.Report(100);
            log.LogInformation(
                "Read-only discovery audit complete: {Total} series, {Existing} existing theme(s), "
                + "{MissingPath} missing path(s), {Candidates} metadata candidate(s), "
                + "{Rejected} rejected, {Transient} transient, {Unavailable} not applicable. "
                + "No audio downloaded or theme files written. Report: {Path}",
                items.Count, skippedExisting, noPath, chosen, rejected, transient, unavailable, path);
        }
        finally
        {
            ThemeMusicFinderGate.AttemptsFile.Release();
        }
    }

    private sealed class RejectAuditDownloader : IThemeAudioDownloader
    {
        public Task<byte[]> DownloadMp3Async(Uri source, CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Audit invariant: an audio downloader was invoked in read-only mode.");
    }
}

internal sealed class ThemeDiscoveryAuditReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int TotalSeriesScanned { get; init; }
    public int ExistingThemesSkipped { get; init; }
    public int MissingPathsSkipped { get; init; }
    public int CandidateCount { get; init; }
    public int RejectedCount { get; init; }
    public int TransientCount { get; init; }
    public int NotApplicableCount { get; init; }
    public required IReadOnlyList<ThemeDiscoveryAuditEntry> Entries { get; init; }

    internal static async Task WriteAtomicAsync(
        string path, ThemeDiscoveryAuditReport report, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Report needs a parent directory", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, report, Options, ct)
                    .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed record ThemeDiscoveryAuditEntry(
    string Title,
    string? OriginalTitle,
    int? Year,
    string? TvdbId,
    string? TmdbId,
    string Path,
    string Status,
    string Reason,
    string? Source,
    string? SourceUrl,
    string? CandidateTitle,
    int? ConfidenceScore);
