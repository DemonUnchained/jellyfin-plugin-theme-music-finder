using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

/// <summary>The orchestration layer runs unattended over a few hundred series and writes into
/// the user's media library, so these tests are aimed squarely at the failure modes that would
/// be invisible in production: a theme quietly overwritten, a written theme never surfaced
/// because the refresh was skipped, a bad hour upstream recorded as a permanent "no theme", and
/// a single sick series silently ending the sweep for everything after it.</summary>
public sealed class ThemeDownloadServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("themesongs-svc-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp dir we couldn't clean up is not a test failure.
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    private static byte[] Mp3(int size = 400_000, byte marker = 0x11)
    {
        var b = new byte[size];
        b[0] = (byte)'I';
        b[1] = (byte)'D';
        b[2] = (byte)'3';
        b[^1] = marker;
        return b;
    }

    /// <summary>A series with a real, existing folder.</summary>
    private Series SeriesWithFolder(string name, string tvdbId)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return MakeSeries(name, dir, tvdbId);
    }

    private static Series MakeSeries(string name, string? path, string? tvdbId)
    {
        var series = new Series { Name = name, Path = path! };
        if (tvdbId is not null) series.SetProviderId(MetadataProvider.Tvdb, tvdbId);
        return series;
    }

    private static string ThemePath(Series series) => Path.Combine(series.Path, "theme.mp3");

    private sealed class Harness
    {
        public required string AttemptsPath { get; init; }
        public required AttemptStore Store { get; init; }
        public required FakeThemeProvider Provider { get; init; }
        public required FakeProviderManager Providers { get; init; }
        public required FakeLibraryManager Library { get; init; }
        public required RecordingLogger<ThemeDownloadService> Log { get; init; }
        public required DelayRecorder Delays { get; init; }
        public required ThemeDownloadService Service { get; init; }
    }

    private Harness Build(
        IEnumerable<Series> series,
        Func<string, ThemeFetchResult>? respond = null,
        string? attemptsPath = null)
    {
        var attempts = attemptsPath ?? Path.Combine(
            Directory.CreateTempSubdirectory("themesongs-state-").FullName, "attempts.json");
        var store = new AttemptStore(attempts);
        var provider = new FakeThemeProvider(respond ?? (_ => ThemeFetchResult.Found(Mp3())));
        var providers = new FakeProviderManager();
        var library = new FakeLibraryManager(series.Cast<BaseItem>().ToArray());
        var log = new RecordingLogger<ThemeDownloadService>();
        var delays = new DelayRecorder();

        return new Harness
        {
            AttemptsPath = attempts,
            Store = store,
            Provider = provider,
            Providers = providers,
            Library = library,
            Log = log,
            Delays = delays,
            Service = new ThemeDownloadService(
                library, providers, provider, store, new FakeFileSystem(), log)
            {
                DelayAsync = delays.DelayAsync
            }
        };
    }

    // ---- I1: never overwrites an existing theme ----------------------------------------

    /// <summary>The plugin's central promise. An existing theme.mp3 is the user's file — it may
    /// be hand-picked — and must never be replaced, nor even prompt an upstream request.</summary>
    [Fact]
    public async Task SeriesThatAlreadyHasAThemeIsSkippedEntirely()
    {
        var series = SeriesWithFolder("Existing", "371572");
        const string sentinel = "the user's own theme";
        await File.WriteAllTextAsync(ThemePath(series), sentinel);

        var h = Build([series]);
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Empty(h.Provider.Requested);
        Assert.Empty(h.Providers.Refreshed);
        Assert.Empty(h.Delays.Delays);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(ThemePath(series)));
    }

    [Fact]
    public async Task RunForSeriesReportsSkippedWhenAThemeIsAlreadyPresent()
    {
        var series = SeriesWithFolder("Existing", "371572");
        await File.WriteAllTextAsync(ThemePath(series), "mine");

        var h = Build([series]);

        Assert.Equal(
            ThemeDownloadService.Outcome.Skipped,
            await h.Service.RunForSeriesAsync(series, CancellationToken.None));
        Assert.Empty(h.Provider.Requested);
    }

    // ---- I2: a written theme is always refreshed ---------------------------------------

    /// <summary>Jellyfin does not surface ThemeMedia until the item is refreshed, so a write
    /// without a refresh looks exactly like a plugin that does nothing. Every series that gets a
    /// file must get a refresh.</summary>
    [Fact]
    public async Task EverySeriesThatGetsAFileIsRefreshed()
    {
        var a = SeriesWithFolder("A", "1");
        var b = SeriesWithFolder("B", "2");
        var c = SeriesWithFolder("C", "3");

        var h = Build([a, b, c]);
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(3, written);
        Assert.All([a, b, c], s => Assert.True(File.Exists(ThemePath(s))));
        Assert.Equal(new BaseItem[] { a, b, c }, h.Providers.Refreshed);
    }

    /// <summary>The contrapositive: nothing on disk means nothing to surface, so a refresh here
    /// would be pointless churn against every series in the library, every night.</summary>
    [Fact]
    public async Task NothingIsRefreshedWhenNoFileWasWritten()
    {
        var series = SeriesWithFolder("NoTheme", "444904");
        var h = Build([series], _ => ThemeFetchResult.NotFound("404"));

        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Empty(h.Providers.Refreshed);
    }

    // ---- I3: only a validated body reaches disk ----------------------------------------

    [Fact]
    public async Task WritesExactlyTheBytesTheProviderReturned()
    {
        var series = SeriesWithFolder("Bytes", "371572");
        var body = Mp3(64 * 1024, marker: 0x5A);

        var h = Build([series], _ => ThemeFetchResult.Found(body));
        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(ThemePath(series)));
    }

    /// <summary>Only <see cref="ThemeFetchStatus.Found"/> carries validated MP3 bytes. Writing on
    /// any other status would put a 404 page, or nothing at all, on disk under a name that then
    /// looks like a real theme forever after.</summary>
    [Theory]
    [InlineData(ThemeFetchStatus.NotFound)]
    [InlineData(ThemeFetchStatus.Transient)]
    public async Task NeverWritesAFileForANonFoundResult(ThemeFetchStatus status)
    {
        var series = SeriesWithFolder("Unvalidated", "444904");
        var result = status == ThemeFetchStatus.NotFound
            ? ThemeFetchResult.NotFound("404")
            : ThemeFetchResult.Transient("503");

        var h = Build([series], _ => result);
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.False(File.Exists(ThemePath(series)));
        Assert.Empty(Directory.GetFiles(series.Path));
    }

    /// <summary>A failing series must not stop the successful ones around it from being written,
    /// and must not leave a partial file of its own behind.</summary>
    [Fact]
    public async Task OnlyTheFoundSeriesGetsAFile()
    {
        var good = SeriesWithFolder("Good", "1");
        var missing = SeriesWithFolder("Missing", "2");
        var broken = SeriesWithFolder("Broken", "3");

        var h = Build([good, missing, broken], id => id switch
        {
            "1" => ThemeFetchResult.Found(Mp3()),
            "2" => ThemeFetchResult.NotFound("404"),
            _ => ThemeFetchResult.Transient("502")
        });

        Assert.Equal(1, await h.Service.RunAsync(null, CancellationToken.None));
        Assert.True(File.Exists(ThemePath(good)));
        Assert.False(File.Exists(ThemePath(missing)));
        Assert.False(File.Exists(ThemePath(broken)));
    }

    // ---- I4: no TVDB id is normal, not an error ----------------------------------------

    /// <summary>A YouTube-channel "series" has no TVDB id. Recording a failure for it would be
    /// meaningless (the key is the id it doesn't have) and logging an error would train the user
    /// to ignore the log.</summary>
    [Fact]
    public async Task SeriesWithoutATvdbIdIsSkippedSilently()
    {
        var noId = MakeSeries("Channel", Path.Combine(_root, "Channel"), tvdbId: null);
        Directory.CreateDirectory(noId.Path);

        var h = Build([noId]);
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Empty(h.Provider.Requested);
        Assert.Empty(h.Delays.Delays);
        Assert.Empty(h.Log.AtLevel(LogLevel.Warning));
        Assert.Empty(h.Log.AtLevel(LogLevel.Error));
        // Nothing was recorded, so nothing is backed off.
        Assert.Equal("{}", await File.ReadAllTextAsync(h.AttemptsPath));
    }

    [Fact]
    public async Task SeriesWithOnlyATmdbIdCanUseTheFallback()
    {
        var dir = Path.Combine(_root, "TmdbOnly");
        Directory.CreateDirectory(dir);
        var series = MakeSeries("TmdbOnly", dir, tvdbId: null);
        series.SetProviderId(MetadataProvider.Tmdb, "1399");
        var h = Build([series]);

        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(["tmdb:1399"], h.Provider.Requested);
        Assert.True(File.Exists(ThemePath(series)));
    }

    [Fact]
    public async Task SeriesWithoutAPathIsSkippedSilently()
    {
        var noPath = MakeSeries("Phantom", path: null, tvdbId: "371572");

        var h = Build([noPath]);
        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Empty(h.Provider.Requested);
        Assert.Empty(h.Log.AtLevel(LogLevel.Error));
    }

    // ---- I5: permanent versus transient -------------------------------------------------

    /// <summary>"This series has no theme" is a fact about the upstream catalogue and is the one
    /// answer worth remembering, so the nightly sweep stops asking for a week.</summary>
    [Fact]
    public async Task NotFoundRecordsAFailureSoBackoffApplies()
    {
        var series = SeriesWithFolder("Missing", "444904");
        var h = Build([series], _ => ThemeFetchResult.NotFound("404"));

        await h.Service.RunAsync(null, CancellationToken.None);

        var reloaded = new AttemptStore(h.AttemptsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.False(reloaded.ShouldTry("444904", 7, DateTimeOffset.UtcNow));
    }

    /// <summary>C2 regression. A 5xx, a rate limit or a truncated body says nothing about whether
    /// a theme exists. Recording it would mark the series themeless for the whole backoff window
    /// on the strength of one bad hour at a free, no-SLA service.</summary>
    [Fact]
    public async Task TransientDoesNotRecordAFailure()
    {
        var series = SeriesWithFolder("Flaky", "444904");
        var h = Build([series], _ => ThemeFetchResult.Transient("503 Service Unavailable"));

        await h.Service.RunAsync(null, CancellationToken.None);

        var reloaded = new AttemptStore(h.AttemptsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.True(reloaded.ShouldTry("444904", 7, DateTimeOffset.UtcNow));
        Assert.Empty(h.Log.AtLevel(LogLevel.Error));
    }

    /// <summary>Same reasoning one layer down: a socket-level failure never reaches the provider's
    /// classifier at all, so the service has to treat it as transient itself.</summary>
    [Fact]
    public async Task HttpRequestExceptionDoesNotRecordAFailure()
    {
        var series = SeriesWithFolder("Unreachable", "444904");
        var h = Build([series], _ => throw new HttpRequestException("connection refused"));

        await h.Service.RunAsync(null, CancellationToken.None);

        var reloaded = new AttemptStore(h.AttemptsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.True(reloaded.ShouldTry("444904", 7, DateTimeOffset.UtcNow));
        Assert.Single(h.Log.AtLevel(LogLevel.Warning));
    }

    /// <summary>A transient answer still counts as an upstream request, so it must be reported as
    /// Fetched rather than Skipped — otherwise the caller skips the throttle and hammers an
    /// already-struggling upstream exactly when it is failing.</summary>
    [Theory]
    [InlineData(ThemeFetchStatus.NotFound)]
    [InlineData(ThemeFetchStatus.Transient)]
    public async Task AFailedLookupIsReportedAsFetchedNotSkipped(ThemeFetchStatus status)
    {
        var series = SeriesWithFolder("Asked", "444904");
        var result = status == ThemeFetchStatus.NotFound
            ? ThemeFetchResult.NotFound("404")
            : ThemeFetchResult.Transient("503");
        var h = Build([series], _ => result);

        Assert.Equal(
            ThemeDownloadService.Outcome.Fetched,
            await h.Service.RunForSeriesAsync(series, CancellationToken.None));
    }

    // ---- I6: backoff is honoured --------------------------------------------------------

    /// <summary>The recorded history has to be loaded and consulted, not just written. A series
    /// inside its retry window makes no request at all.</summary>
    [Fact]
    public async Task SeriesInsideItsRetryWindowIsNotRefetched()
    {
        var attempts = Path.Combine(
            Directory.CreateTempSubdirectory("themesongs-state-").FullName, "attempts.json");
        var seed = new AttemptStore(attempts);
        seed.RecordFailure("444904", DateTimeOffset.UtcNow.AddDays(-1));
        await seed.SaveAsync(CancellationToken.None);

        var series = SeriesWithFolder("BackedOff", "444904");
        var h = Build([series], attemptsPath: attempts);

        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Empty(h.Provider.Requested);
        Assert.Empty(h.Delays.Delays);
        Assert.False(File.Exists(ThemePath(series)));
    }

    [Fact]
    public async Task SeriesPastItsRetryWindowIsRefetched()
    {
        var attempts = Path.Combine(
            Directory.CreateTempSubdirectory("themesongs-state-").FullName, "attempts.json");
        var seed = new AttemptStore(attempts);
        seed.RecordFailure("444904", DateTimeOffset.UtcNow.AddDays(-30));
        await seed.SaveAsync(CancellationToken.None);

        var series = SeriesWithFolder("Eligible", "444904");
        var h = Build([series], attemptsPath: attempts);

        Assert.Equal(1, await h.Service.RunAsync(null, CancellationToken.None));
        Assert.Equal(["444904"], h.Provider.Requested);
    }

    // ---- I7: one bad series cannot abort the sweep --------------------------------------

    /// <summary>C-class regression. An unguarded throw here silently skipped every remaining
    /// series — a 300-series library could go one show deep and report success.</summary>
    [Fact]
    public async Task AnExceptionFromOneSeriesDoesNotAbortTheSweep()
    {
        var first = SeriesWithFolder("First", "1");
        var sick = SeriesWithFolder("Sick", "2");
        var last = SeriesWithFolder("Last", "3");

        var h = Build([first, sick, last], id => id == "2"
            ? throw new InvalidOperationException("corrupt metadata")
            : ThemeFetchResult.Found(Mp3()));

        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.True(File.Exists(ThemePath(first)));
        Assert.True(File.Exists(ThemePath(last)));
        Assert.Contains(h.Log.AtLevel(LogLevel.Warning), e => e.Exception is InvalidOperationException);
    }

    /// <summary>C-class regression, the specific one. TaskCanceledException derives from
    /// OperationCanceledException, so an HttpClient *timeout* is indistinguishable from a user
    /// cancellation by type alone. An unfiltered catch treated it as cancellation and ended the
    /// whole run; the filter on ct.IsCancellationRequested is what keeps a slow upstream from
    /// silently truncating the sweep.</summary>
    [Fact]
    public async Task AnHttpTimeoutIsNotMistakenForCancellationAndDoesNotAbortTheSweep()
    {
        var first = SeriesWithFolder("First", "1");
        var slow = SeriesWithFolder("Slow", "2");
        var last = SeriesWithFolder("Last", "3");

        var h = Build([first, slow, last], id => id == "2"
            ? throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException())
            : ThemeFetchResult.Found(Mp3()));

        // The run's own token is never cancelled — that is the whole point.
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.True(File.Exists(ThemePath(first)));
        Assert.True(File.Exists(ThemePath(last)));
        Assert.Contains(h.Log.AtLevel(LogLevel.Warning), e => e.Exception is TaskCanceledException);
    }

    /// <summary>The refresh is the last step and reaches deep into Jellyfin's metadata stack;
    /// a fault there must be contained like any other.</summary>
    [Fact]
    public async Task ARefreshFailureDoesNotAbortTheSweep()
    {
        var first = SeriesWithFolder("First", "1");
        var second = SeriesWithFolder("Second", "2");

        var h = Build([first, second]);
        h.Providers.OnRefresh = item =>
        {
            if (item.Name == "First") throw new InvalidOperationException("locked metadata");
        };

        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(2, h.Providers.Refreshed.Count);
        Assert.True(File.Exists(ThemePath(second)));
    }

    // ---- I8: real cancellation still stops the run, and still saves ---------------------

    /// <summary>Cancellation of *this* run must genuinely stop it. The token is cancelled while
    /// the second series is being fetched, so the guard's exception filter matches and rethrows
    /// instead of swallowing it into the per-series catch.</summary>
    [Fact]
    public async Task RealCancellationStopsTheRun()
    {
        var first = SeriesWithFolder("First", "1");
        var second = SeriesWithFolder("Second", "2");
        var third = SeriesWithFolder("Third", "3");

        using var cts = new CancellationTokenSource();
        var h = Build([first, second, third], id =>
        {
            if (id != "2") return ThemeFetchResult.Found(Mp3());
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Service.RunAsync(null, cts.Token));

        Assert.Equal(["1", "2"], h.Provider.Requested);
        Assert.False(File.Exists(ThemePath(third)));
    }

    /// <summary>Cancellation between series stops the sweep at the top of the next iteration.</summary>
    [Fact]
    public async Task CancellationBetweenSeriesStopsTheRun()
    {
        var first = SeriesWithFolder("First", "1");
        var second = SeriesWithFolder("Second", "2");

        using var cts = new CancellationTokenSource();
        var h = Build([first, second]);
        h.Delays.OnDelay = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Service.RunAsync(null, cts.Token));

        Assert.Equal(["1"], h.Provider.Requested);
    }

    /// <summary>A cancelled run must not throw away what it learned. Anything recorded before the
    /// cancellation is still persisted, otherwise a nightly sweep that keeps getting cancelled
    /// re-asks the upstream for the same missing themes forever.</summary>
    [Fact]
    public async Task ACancelledRunStillSavesTheFailuresItRecorded()
    {
        var missing = SeriesWithFolder("Missing", "444904");
        var second = SeriesWithFolder("Second", "2");

        using var cts = new CancellationTokenSource();
        var h = Build([missing, second], id => id == "444904"
            ? ThemeFetchResult.NotFound("404")
            : ThemeFetchResult.Found(Mp3()));
        h.Delays.OnDelay = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Service.RunAsync(null, cts.Token));

        var reloaded = new AttemptStore(h.AttemptsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.False(reloaded.ShouldTry("444904", 7, DateTimeOffset.UtcNow));
    }

    // ---- I9: upstream throttling --------------------------------------------------------

    /// <summary>Roughly one request per second against a free service that owes us nothing.</summary>
    [Fact]
    public async Task PaysAOneSecondThrottleBetweenUpstreamRequests()
    {
        var series = Enumerable.Range(1, 3).Select(i => SeriesWithFolder($"S{i}", i.ToString())).ToList();
        var h = Build(series, _ => ThemeFetchResult.NotFound("404"));

        await h.Service.RunAsync(null, CancellationToken.None);

        // Three requests, two gaps: the wait belongs between requests, not after the last one.
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], h.Delays.Delays);
    }

    [Fact]
    public async Task DoesNotThrottleAfterTheFinalSeries()
    {
        var only = SeriesWithFolder("Only", "1");
        var h = Build([only]);

        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Empty(h.Delays.Delays);
    }

    /// <summary>A skipped series made no upstream request, so it must not pay for one. A library
    /// scan that bulk-adds hundreds of already-themed series would otherwise stall for minutes
    /// doing nothing at all.</summary>
    [Fact]
    public async Task SkippedSeriesDoNotPayTheThrottle()
    {
        var alreadyThemed = SeriesWithFolder("Themed", "1");
        await File.WriteAllTextAsync(ThemePath(alreadyThemed), "mine");
        var fetched = SeriesWithFolder("Fetched", "2");
        var noId = MakeSeries("Channel", Path.Combine(_root, "Channel"), tvdbId: null);
        Directory.CreateDirectory(noId.Path);
        var last = SeriesWithFolder("Last", "4");

        var h = Build([alreadyThemed, fetched, noId, last]);
        await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(["2", "4"], h.Provider.Requested);
        // Only the non-final series that actually made a request pays.
        Assert.Equal([TimeSpan.FromSeconds(1)], h.Delays.Delays);
    }

    // ---- I10: an unwritable library is reported once per run ----------------------------

    /// <summary>305 identical write errors buries the one line that matters, so the error is
    /// logged once per run — but exactly once, not zero times.</summary>
    [Fact]
    public async Task AnUnwritableLibraryIsLoggedOncePerRunNotOncePerSeries()
    {
        // A path that does not exist makes the atomic write fail with a DirectoryNotFoundException
        // (an IOException), which is the same shape as a read-only or full library.
        var series = Enumerable.Range(1, 4)
            .Select(i => MakeSeries($"S{i}", Path.Combine(_root, "gone", $"S{i}"), i.ToString()))
            .ToList();

        var h = Build(series);
        var written = await h.Service.RunAsync(null, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Single(h.Log.AtLevel(LogLevel.Error));
        Assert.Equal(4, h.Provider.Requested.Count); // every series was still attempted
        Assert.Empty(h.Providers.Refreshed);
    }

    /// <summary>The single log line has to carry the exception and the path: "library is not
    /// writable" is actively misleading for a full disk or a refused overwrite.</summary>
    [Fact]
    public async Task TheWriteFailureLogNamesThePathAndCarriesTheException()
    {
        var series = MakeSeries("S", Path.Combine(_root, "gone", "S"), "1");
        var h = Build([series]);

        await h.Service.RunAsync(null, CancellationToken.None);

        var error = Assert.Single(h.Log.AtLevel(LogLevel.Error));
        Assert.Contains(ThemePath(series), error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(error.Exception);
    }

    /// <summary>Collapsing the outcome to a bool would discard Unwritable, which is the only
    /// signal the item-added path has that a read-only library ate a new series.</summary>
    [Fact]
    public async Task RunForSeriesReportsUnwritable()
    {
        var series = MakeSeries("S", Path.Combine(_root, "gone", "S"), "1");
        var h = Build([series]);

        Assert.Equal(
            ThemeDownloadService.Outcome.Unwritable,
            await h.Service.RunForSeriesAsync(series, CancellationToken.None));
    }

    // ---- I11: progress reporting --------------------------------------------------------

    [Fact]
    public async Task ProgressIsMonotonicWithinRangeAndEndsAt100()
    {
        var series = Enumerable.Range(1, 4).Select(i => SeriesWithFolder($"S{i}", i.ToString())).ToList();
        var reports = new List<double>();

        var h = Build(series);
        await h.Service.RunAsync(new Progress<double>(reports.Add), CancellationToken.None);

        // Progress<T> posts asynchronously; wait for the reports to land.
        await WaitFor(() => reports.Count >= series.Count + 1);

        Assert.All(reports, p => Assert.InRange(p, 0, 100));
        Assert.Equal(reports.OrderBy(p => p), reports);
        Assert.Equal(100, reports[^1]);
        Assert.Equal(0, reports[0]);
    }

    /// <summary>An empty library divides by the series count. Zero must not produce NaN — which
    /// is not merely ugly: NaN propagates into Jellyfin's task progress and sticks there.</summary>
    [Fact]
    public async Task EmptyLibraryReportsCleanProgressAndDoesNotDivideByZero()
    {
        var reports = new List<double>();
        var h = Build([]);

        var written = await h.Service.RunAsync(new Progress<double>(reports.Add), CancellationToken.None);
        await WaitFor(() => reports.Count >= 1);

        Assert.Equal(0, written);
        Assert.All(reports, p => Assert.False(double.IsNaN(p) || double.IsInfinity(p)));
        Assert.Equal(100, reports[^1]);
    }

    [Fact]
    public async Task ANullProgressIsAccepted()
    {
        var h = Build([SeriesWithFolder("S", "1")]);
        Assert.Equal(1, await h.Service.RunAsync(null, CancellationToken.None));
    }

    // ---- library query ------------------------------------------------------------------

    /// <summary>The sweep has to ask for every series in the library, recursively; a
    /// non-recursive query returns only top-level items and would quietly cover a fraction of
    /// a nested library.</summary>
    [Fact]
    public async Task AsksTheLibraryForEverySeriesRecursively()
    {
        var h = Build([SeriesWithFolder("S", "1")]);
        await h.Service.RunAsync(null, CancellationToken.None);

        var query = Assert.Single(h.Library.Queries);
        Assert.True(query.Recursive);
        Assert.False(query.IsVirtualItem);
        Assert.Equal([BaseItemKind.Series], query.IncludeItemTypes);
    }

    /// <summary>Polls instead of sleeping: Progress&lt;T&gt; marshals its callbacks through the
    /// synchronization context, so the reports are not guaranteed to have arrived the instant
    /// RunAsync returns.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
    }
}
