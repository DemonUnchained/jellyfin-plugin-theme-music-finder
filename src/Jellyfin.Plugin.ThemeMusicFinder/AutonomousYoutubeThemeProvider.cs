using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>
/// Last-resort discovery from YouTube, SoundCloud, and anime-specific Bilibili searches.
/// Every external URL is allowlisted, every shortlisted hit receives a second metadata lookup,
/// and no result is written unless the decision engine finds one clear winner. Discovery is
/// best-effort: one dead source cannot cause other sources to be assumed to lack a theme.
/// </summary>
internal sealed class AutonomousThemeDiscoveryProvider(
    IApplicationPaths appPaths,
    IThemeAudioDownloader downloader,
    ILibraryManager? libraryManager = null,
    bool enableSoundCloud = true,
    bool enableBilibili = true,
    string? executablePathOverride = null,
    bool auditOnly = false) : IThemeProvider
{
    private readonly string _executable = executablePathOverride ??
        Path.Combine(appPaths.ProgramDataPath, "trailer-tools", "yt-dlp");
    // Injectable seams let tests run the entire discovery/decision/download orchestration
    // without contacting live public services, making incorrect writes reproducible.
    internal Func<string, string, CancellationToken, Task<IReadOnlyList<ThemeSearchVideo>>>? SearchOverride { get; init; }
    internal Func<ThemeSearchVideo, CancellationToken, Task<ThemeSearchVideo>>? InspectOverride { get; init; }
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    private readonly HashSet<string> _sourceUnavailable = new(StringComparer.Ordinal);
    private bool _toolUnavailable;

    private const int MaxSearchResults = 5;
    private const int MaxInspectedPerSource = 2;
    private const int MaxInspections = 6;

    private sealed record SearchSource(string Name, string Prefix, int Queries);

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        if (_toolUnavailable || !File.Exists(_executable))
            return ThemeFetchResult.NotApplicable("Autonomous discovery needs the installed yt-dlp tool");

        var queries = ThemeCandidateDecisionEngine.BuildQueries(
            series.Name, series.OriginalTitle, series.ProductionYear);
        if (queries.Count == 0)
            return ThemeFetchResult.NotApplicable("series has no searchable title");

        var sources = new List<SearchSource> { new("YouTube", "ytsearch", 3) };
        if (enableSoundCloud) sources.Add(new("SoundCloud", "scsearch", 2));
        if (enableBilibili && IsLikelyAnime(series))
            sources.Add(new("Bilibili", "bilisearch", 2));

        var indexed = new Dictionary<string, ThemeSearchVideo>(StringComparer.Ordinal);
        var errors = new List<string>();
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        var attemptedSearch = false;

        foreach (var source in sources)
        {
            if (_sourceUnavailable.Contains(source.Name)) continue;
            for (var i = 0; i < Math.Min(source.Queries, queries.Count); i++)
            {
                ct.ThrowIfCancellationRequested();
                if (attemptedSearch)
                    await DelayAsync(TimeSpan.FromMilliseconds(900), ct).ConfigureAwait(false);
                attemptedSearch = true;

                try
                {
                    foreach (var candidate in await SearchCandidateAsync(source, GetSourceQuery(source.Name, queries, i), ct).ConfigureAwait(false))
                    {
                        var key = source.Name + ":" + candidate.Id;
                        indexed[key] = indexed.TryGetValue(key, out var prior)
                            ? candidate with { SearchHits = prior.SearchHits + 1 }
                            : candidate;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors.Add(source.Name + " search: " + Trim(ex.Message));
                    // Repeated 403/429/timeouts from the same service during a sweep should
                    // not be retried for hundreds of other titles in the same run.
                    if (ex is TimeoutException ||
                        ex.Message.Contains("429", StringComparison.Ordinal) ||
                        ex.Message.Contains("403", StringComparison.Ordinal))
                        _sourceUnavailable.Add(source.Name);
                    break;
                }
            }
        }

        // A flat search often omits duration. The 90s placeholder is used ONLY to select
        // candidates for second-stage metadata inspection, NEVER to approve or download one.
        var preliminary = indexed.Values
            .Where(video => ThemeCandidateDecisionEngine.HasExactTitle(
                series.Name, series.OriginalTitle, video.Title))
            .Select(video => ThemeCandidateDecisionEngine.Evaluate(
                series.Name, series.OriginalTitle, series.ProductionYear,
                video with { Duration = video.Duration ?? TimeSpan.FromSeconds(90) }))
            .Where(result =>
            {
                if (result.Accepted) return true;
                rejected[result.Reason] = rejected.GetValueOrDefault(result.Reason) + 1;
                return false;
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Video.Source, StringComparer.Ordinal)
            .ThenBy(result => result.Video.Id, StringComparer.Ordinal)
            .GroupBy(result => result.Video.Source)
            .SelectMany(group => group.Take(MaxInspectedPerSource))
            .Take(MaxInspections)
            .ToList();

        var verified = new List<ScoredThemeVideo>();
        foreach (var provisional in preliminary)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detail = await InspectCandidateAsync(provisional.Video, ct).ConfigureAwait(false);
                var scored = ThemeCandidateDecisionEngine.Evaluate(
                    series.Name, series.OriginalTitle, series.ProductionYear, detail);
                if (scored.Accepted)
                    verified.Add(scored);
                else
                    rejected[scored.Reason] = rejected.GetValueOrDefault(scored.Reason) + 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add("inspect " + provisional.Video.Source + ": " + Trim(ex.Message));
            }
        }

        var ranked = verified
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Video.Source, StringComparer.Ordinal)
            .ThenBy(result => result.Video.Id, StringComparer.Ordinal)
            .ToList();

        // Multiple videos can be labeled "official" or share the title of an unrelated
        // score. Abstain rather than choosing among similar-strength possibilities.
        if (ranked.Count > 1 && !ThemeCandidateDecisionEngine.IsClearWinner(ranked))
            return ThemeFetchResult.NotFound(
                "autonomous search found competing themes without a clear score margin; manual review not required for future retries");

        if (ranked.Count > 0)
        {
            var winner = ranked[0];
            if (auditOnly)
                return ThemeFetchResult.PreviewCandidate(
                    winner.Video.Source,
                    winner.Video.Url!,
                    winner.Video.Title,
                    winner.Score,
                    winner.Reason + "; metadata-only selection; audio not checked or downloaded");
            try
            {
                var bytes = await downloader.DownloadMp3Async(winner.Video.Url!, ct).ConfigureAwait(false);
                return ThemeFile.IsValidMp3(bytes, "audio/mpeg")
                    ? ThemeFetchResult.Found(bytes,
                        "Autonomous " + winner.Video.Source + " (score " + winner.Score + ")",
                        winner.Video.Url)
                    : ThemeFetchResult.CandidateUnavailable(
                        "autonomous top-ranked theme did not produce a valid MP3");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add("download " + winner.Video.Source + ": " + Trim(ex.Message));
            }
        }

        if (errors.Count > 0 || _sourceUnavailable.Count > 0)
            return ThemeFetchResult.Transient(
                "autonomous discovery incomplete (retry next sweep): "
                + (errors.Count > 0 ? string.Join("; ", errors.Take(3)) : "provider circuit open")
                + (_sourceUnavailable.Count > 0
                    ? "; temporarily unavailable: " + string.Join(", ", _sourceUnavailable)
                    : ""));

        var topRejections = string.Join("; ",
            rejected.OrderByDescending(pair => pair.Value).Take(3)
                .Select(pair => pair.Value + " × " + pair.Key));
        return ThemeFetchResult.NotFound(
            "automatic discovery evaluated " + indexed.Count
            + " candidates but found no verified theme"
            + (topRejections.Length > 0 ? "; rejected: " + topRejections : ""));
    }

    // The second alternative-source search should try an original-language title if one
    // exists. This matters particularly for Japanese names on Bilibili and SoundCloud.
    internal static string GetSourceQuery(string source, IReadOnlyList<string> queries, int index)
        => source != "YouTube" && index == 1 && queries.Count >= 3
            ? queries[2]
            : queries[index];

    private bool IsLikelyAnime(Series series)
    {
        if (series.Genres.Any(g =>
            g.Equals("Anime", StringComparison.OrdinalIgnoreCase) ||
            g.Equals("Animation", StringComparison.OrdinalIgnoreCase))) return true;
        if (libraryManager is null) return false;
        try
        {
            return AnimeLibraryThemeProvider.IsLikelyAnime(
                series, libraryManager.GetCollectionFolders(series));
        }
        catch { return false; } // Applicability is an optimization, not a scan error.
    }

    private Task<IReadOnlyList<ThemeSearchVideo>> SearchCandidateAsync(
        SearchSource source, string query, CancellationToken ct)
        => SearchOverride is null
            ? SearchAsync(source, query, ct)
            : SearchOverride(source.Name, query, ct);

    private Task<ThemeSearchVideo> InspectCandidateAsync(
        ThemeSearchVideo candidate, CancellationToken ct)
        => InspectOverride is null
            ? InspectAsync(candidate, ct)
            : InspectOverride(candidate, ct);

    private async Task<IReadOnlyList<ThemeSearchVideo>> SearchAsync(
        SearchSource source, string query, CancellationToken ct)
    {
        var arguments = new[]
        {
            "--ignore-config", "--no-playlist", "--skip-download",
            "--dump-single-json", "--flat-playlist", "--no-warnings",
            "--socket-timeout", "12", "--retries", "1", "--extractor-retries", "1",
            source.Prefix + MaxSearchResults + ":" + query
        };
        var output = await RunYtDlpAsync(arguments, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        return ParseSearchOutput(output, source.Name);
    }

    private async Task<ThemeSearchVideo> InspectAsync(ThemeSearchVideo video, CancellationToken ct)
    {
        if (video.Url is null || !IsAllowedUri(video.Source, video.Url))
            throw new InvalidOperationException("Candidate URL was not verified for its source");

        var output = await RunYtDlpAsync(
            [
                "--ignore-config", "--no-playlist", "--skip-download",
                "--dump-single-json", "--no-warnings", "--socket-timeout", "12",
                "--retries", "1", "--extractor-retries", "1", video.Url.ToString()
            ], TimeSpan.FromSeconds(35), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Candidate inspection returned no metadata");
        if (!IsSameCandidate(video, root))
            throw new InvalidDataException("Candidate identity changed between search and full inspection");

        var title = String(root, "title");
        var seconds = Number(root, "duration");
        if (string.IsNullOrWhiteSpace(title) || seconds is null)
            throw new InvalidDataException("Candidate inspection did not return title and duration");

        var detailedUrl = String(root, "webpage_url");
        if (detailedUrl is not null
            && (!Uri.TryCreate(detailedUrl, UriKind.Absolute, out var parsed)
                || !IsAllowedUri(video.Source, parsed)))
            throw new InvalidDataException("Candidate changed origin during metadata inspection");

        return video with
        {
            Title = title,
            Duration = seconds is >= 0 and <= 86400 ? TimeSpan.FromSeconds(seconds.Value) : null,
            Channel = String(root, "channel") ?? String(root, "uploader") ?? video.Channel,
            Description = String(root, "description")
        };
    }

    private async Task<string> RunYtDlpAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                _toolUnavailable = true;
                throw new InvalidOperationException("yt-dlp could not start");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _toolUnavailable = true;
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("yt-dlp metadata lookup exceeded " + timeout.TotalSeconds + " seconds");
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("yt-dlp returned " + process.ExitCode + ": " + Trim(error));
        if (output.Length > 2_000_000)
            throw new InvalidDataException("yt-dlp returned unusually large metadata");
        return output;
    }

    internal static bool IsSameCandidate(ThemeSearchVideo selected, JsonElement details)
    {
        var detailId = String(details, "id");
        // A search result must not silently redirect to another same-host recording.
        // If metadata supplies a stable identity, it must match the shortlisted identity.
        return !string.IsNullOrWhiteSpace(detailId)
            && !string.IsNullOrWhiteSpace(selected.Id)
            && string.Equals(detailId, selected.Id, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<ThemeSearchVideo> ParseSearchOutput(string output, string source)
    {
        using var json = JsonDocument.Parse(output);
        if (!json.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array) return [];

        var results = new List<ThemeSearchVideo>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = String(entry, "id") ?? "";
            var title = String(entry, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var url = GetCandidateUrl(source, entry);
            if (url is null) continue;
            var duration = Number(entry, "duration");
            results.Add(new ThemeSearchVideo(
                id.Length > 0 ? id : url.ToString(), title,
                String(entry, "channel") ?? String(entry, "uploader") ?? "",
                duration is >= 0 and <= 86400 ? TimeSpan.FromSeconds(duration.Value) : null,
                Source: source, Url: url));
        }
        return results;
    }

    internal static Uri? GetCandidateUrl(string source, JsonElement item)
    {
        var id = String(item, "id") ?? "";
        if (source == "YouTube" && id.Length == 11
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return new Uri("https://www.youtube.com/watch?v=" + id);

        if (source == "Bilibili" && id.StartsWith("BV", StringComparison.Ordinal)
            && id.Length == 12 && id.All(char.IsAsciiLetterOrDigit))
            return new Uri("https://www.bilibili.com/video/" + id);

        foreach (var key in new[] { "webpage_url", "url" })
        {
            var raw = String(item, key);
            if (Uri.TryCreate(raw, UriKind.Absolute, out var url) && IsAllowedUri(source, url))
                return url;
        }
        return null;
    }

    internal static bool IsAllowedUri(string source, Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0 ||
            url.Port != 443) return false;

        var host = url.Host.ToLowerInvariant();
        return source switch
        {
            "YouTube" => host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
                or "youtu.be",
            "SoundCloud" => host is "soundcloud.com" or "www.soundcloud.com"
                or "m.soundcloud.com" or "api.soundcloud.com"
                or "api-v2.soundcloud.com",
            "Bilibili" => host is "bilibili.com" or "www.bilibili.com"
                or "m.bilibili.com",
            _ => false
        };
    }

    private static string? String(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? Number(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : null;

    private static string Trim(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 280 ? text : text[..280];
    }
}
