using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>
/// Autonomous last-resort discovery. Searches multiple title-specific phrases and compares
/// metadata locally before downloading. Never trusts the search engine's first-ranked video.
/// Uses yt-dlp when installed; without it, existing stable-ID providers still work.
/// </summary>
internal sealed class AutonomousYoutubeThemeProvider(
    IApplicationPaths appPaths, IThemeAudioDownloader downloader) : IThemeProvider
{
    private readonly string _executable = Path.Combine(appPaths.ProgramDataPath, "trailer-tools", "yt-dlp");
    private bool _unavailable;

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        if (_unavailable || !File.Exists(_executable))
            return ThemeFetchResult.NotApplicable("Automatic YouTube discovery requires the installed yt-dlp tool");

        var queries = ThemeCandidateDecisionEngine.BuildQueries(
            series.Name, series.OriginalTitle, series.ProductionYear);
        if (queries.Count == 0)
            return ThemeFetchResult.NotApplicable("series has no searchable title");

        var indexed = new Dictionary<string, ThemeSearchVideo>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var video in await SearchAsync(query, ct).ConfigureAwait(false))
                {
                    indexed[video.Id] = indexed.TryGetValue(video.Id, out var existing)
                        ? video with { SearchHits = existing.SearchHits + 1 }
                        : video;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        var ranked = indexed.Values
            .Select(video => ThemeCandidateDecisionEngine.Evaluate(
                series.Name, series.OriginalTitle, series.ProductionYear, video))
            .Where(candidate => candidate.Accepted)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Video.Id, StringComparer.Ordinal)
            .ToList();

        // An unclear top result is not an instruction to fetch the first available track.
        // If two equally strong candidates exist, refuse to guess.
        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return ThemeFetchResult.NotFound(
                "automatic discovery found equally strong candidates; no safe winner");

        foreach (var candidate in ranked.Take(3))
        {
            var url = new Uri("https://www.youtube.com/watch?v=" + candidate.Video.Id);
            try
            {
                var bytes = await downloader.DownloadMp3Async(url, ct).ConfigureAwait(false);
                if (ThemeFile.IsValidMp3(bytes, "audio/mpeg"))
                    return ThemeFetchResult.Found(bytes,
                        "Automatic YouTube discovery (score " + candidate.Score + ")", url);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add("download " + candidate.Video.Id + ": " + ex.Message); }
        }

        if (errors.Count > 0)
            return ThemeFetchResult.Transient("automatic discovery incomplete: "
                + string.Join("; ", errors.Take(3)));
        return ThemeFetchResult.NotFound(
            ranked.Count == 0
                ? "automatic discovery found no video with sufficient title/theme confidence"
                : "automatic discovery candidates did not produce validated audio");
    }

    private async Task<IReadOnlyList<ThemeSearchVideo>> SearchAsync(string query, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "--ignore-config", "--no-playlist", "--skip-download", "--dump-single-json",
            "--no-warnings", "--socket-timeout", "12", "--retries", "1",
            "--extractor-retries", "1", "--flat-playlist", "ytsearch5:" + query
        }) info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            _unavailable = true;
            throw new InvalidOperationException("yt-dlp search could not be started");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("automatic search exceeded 40 seconds");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("YouTube search failed: " + error[..Math.Min(error.Length, 250)]);

        using var json = JsonDocument.Parse(output);
        if (!json.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array) return [];

        var results = new List<ThemeSearchVideo>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = String(entry, "id");
            var title = String(entry, "title");
            if (id is null || id.Length != 11 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || string.IsNullOrWhiteSpace(title)) continue;
            var seconds = Number(entry, "duration");
            results.Add(new ThemeSearchVideo(
                id, title, String(entry, "channel") ?? String(entry, "uploader") ?? string.Empty,
                seconds is >= 0 and <= 86400 ? TimeSpan.FromSeconds(seconds.Value) : null));
        }
        return results;
    }

    private static string? String(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? Number(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : null;
}
