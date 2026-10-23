using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
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

            // Stable across restarts, but invalidated when show identities or the enabled
            // alternative sources change. A completed audit always starts fresh.
            var signatureInput = string.Join("\n", items
                .OrderBy(series => series.Path, StringComparer.Ordinal)
                .Select(series => series.Path + "|" + series.Name + "|" + series.ProductionYear))
                + "|soundcloud:" + config.EnableSoundCloudDiscovery
                + "|bilibili:" + config.EnableBilibiliDiscovery;
            var signature = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(signatureInput)));

            var path = Path.Combine(appPaths.PluginConfigurationsPath,
                "ThemeMusicFinder.discovery-audit.json");
            var previous = await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, signature, cancellationToken).ConfigureAwait(false);

            var activePaths = items
                .Where(series => !string.IsNullOrWhiteSpace(series.Path))
                .Where(series => !File.Exists(Path.Combine(series.Path, "theme.mp3")))
                .Select(series => series.Path)
                .ToHashSet(StringComparer.Ordinal);
            var output = previous?.Entries
                .Where(entry => activePaths.Contains(entry.Path)
                    && entry.Status is not ("transient" or "candidate-unavailable"))
                .ToList() ?? [];
            var visited = output.Select(entry => entry.Path)
                .ToHashSet(StringComparer.Ordinal);

            var skippedExisting = items.Count(series =>
                !string.IsNullOrWhiteSpace(series.Path)
                && File.Exists(Path.Combine(series.Path, "theme.mp3")));
            var noPath = items.Count(series => string.IsNullOrWhiteSpace(series.Path));
            var chosen = output.Count(entry => entry.Status == "candidate");
            var rejected = output.Count(entry => entry.Status == "rejected");
            var transient = 0;
            var unavailable = output.Count(entry => entry.Status == "not-applicable");
            var newEvaluations = 0;

            if (visited.Count > 0)
                log.LogInformation("Resuming read-only theme discovery audit with {Count} earlier decisions.",
                    visited.Count);

            async Task SaveCheckpointAsync(bool complete, CancellationToken writeToken)
                => await ThemeDiscoveryAuditReport.WriteAtomicAsync(
                    path,
                    new ThemeDiscoveryAuditReport
                    {
                        ScanSignature = signature,
                        Complete = complete,
                        GeneratedAtUtc = DateTimeOffset.UtcNow,
                        TotalSeriesScanned = items.Count,
                        ExistingThemesSkipped = skippedExisting,
                        MissingPathsSkipped = noPath,
                        CandidateCount = chosen,
                        RejectedCount = rejected,
                        TransientCount = transient,
                        NotApplicableCount = unavailable,
                        Entries = output.ToArray()
                    },
                    writeToken).ConfigureAwait(false);

            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var series = items[i];
                    progress?.Report(items.Count == 0 ? 100 : i * 100.0 / items.Count);
                    if (string.IsNullOrWhiteSpace(series.Path)
                        || File.Exists(Path.Combine(series.Path, "theme.mp3"))
                        || visited.Contains(series.Path))
                        continue;

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

                    newEvaluations++;
                    // Checkpoint every ten evaluated shows automatically. This is an
                    // internal durability threshold, NOT a manual batch requiring input.
                    if (newEvaluations % 10 == 0)
                        await SaveCheckpointAsync(false, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Keep verified metadata decisions from the interrupted sweep so an
                // administrator can simply run the task again to resume.
                try
                {
                    await SaveCheckpointAsync(false, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not save interrupted audit checkpoint to {Path}", path);
                }
                throw;
            }

            await SaveCheckpointAsync(true, cancellationToken).ConfigureAwait(false);

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

    public int SchemaVersion { get; init; } = 2;
    public bool Complete { get; init; } = true;
    public string ScanSignature { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int TotalSeriesScanned { get; init; }
    public int ExistingThemesSkipped { get; init; }
    public int MissingPathsSkipped { get; init; }
    public int CandidateCount { get; init; }
    public int RejectedCount { get; init; }
    public int TransientCount { get; init; }
    public int NotApplicableCount { get; init; }
    public required IReadOnlyList<ThemeDiscoveryAuditEntry> Entries { get; init; }

    internal static async Task<ThemeDiscoveryAuditReport?> LoadIncompleteAsync(
        string path, string signature, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<ThemeDiscoveryAuditReport>(
                stream, Options, ct).ConfigureAwait(false);
            return report is { Complete: false, SchemaVersion: 2 }
                && report.ScanSignature == signature
                && report.Entries is not null
                ? report : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

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
