using Jellyfin.Plugin.ThemeMusicFinder;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public class AttemptStoreTests
{
    private static string TempPath() =>
        Path.Combine(Directory.CreateTempSubdirectory().FullName, "attempts.json");

    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TriesShowNeverAttempted()
        => Assert.True(new AttemptStore(TempPath()).ShouldTry("371572", 7, Now));

    [Fact]
    public void SkipsShowInsideBackoffWindow()
    {
        var s = new AttemptStore(TempPath());
        s.RecordFailure("444904", Now.AddDays(-2));
        Assert.False(s.ShouldTry("444904", 7, Now));
    }

    [Fact]
    public void RetriesShowPastBackoffWindow()
    {
        var s = new AttemptStore(TempPath());
        s.RecordFailure("444904", Now.AddDays(-8));
        Assert.True(s.ShouldTry("444904", 7, Now));
    }

    [Fact]
    public async Task PersistsAcrossReload()
    {
        var path = TempPath();
        var a = new AttemptStore(path);
        a.RecordFailure("444904", Now);
        await a.SaveAsync(CancellationToken.None);

        var b = new AttemptStore(path);
        await b.LoadAsync(CancellationToken.None);
        Assert.False(b.ShouldTry("444904", 7, Now));
    }

    [Fact]
    public async Task LoadOfMissingFileIsNotAnError()
    {
        var s = new AttemptStore(TempPath());
        await s.LoadAsync(CancellationToken.None);
        Assert.True(s.ShouldTry("anything", 7, Now));
    }

    [Fact]
    public async Task LoadOfCorruptFileIsNotAnError()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "{ this is not json");
        var s = new AttemptStore(path);
        await s.LoadAsync(CancellationToken.None);
        Assert.True(s.ShouldTry("anything", 7, Now));
    }

    /// <summary>I1. An unparseable file must not simply be overwritten by the empty store the
    /// failed load produced — the bytes are the user's backoff history and may have been
    /// hand-edited. They are moved aside instead, which also stops a corrupt file from wedging
    /// the store into never saving again.</summary>
    [Fact]
    public async Task CorruptHistoryIsPreservedRatherThanOverwritten()
    {
        var path = TempPath();
        const string original = "{ this is not json";
        await File.WriteAllTextAsync(path, original);

        var s = new AttemptStore(path);
        await s.LoadAsync(CancellationToken.None);
        s.RecordFailure("444904", Now);
        await s.SaveAsync(CancellationToken.None);

        Assert.Equal(original, await File.ReadAllTextAsync(path + ".invalid"));

        var reloaded = new AttemptStore(path);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.False(reloaded.ShouldTry("444904", 7, Now));
    }

    /// <summary>I1. A read that fails for a reason unrelated to the contents (locked by a backup
    /// agent, an NFS hiccup, a permission problem) says nothing about the file being bad. Saving
    /// the empty in-memory store over it would destroy a perfectly good history, so the save is
    /// skipped and the file is left exactly as it was.</summary>
    [Fact]
    public async Task SaveIsSkippedWhenTheHistoryCouldNotBeRead()
    {
        var path = TempPath();
        var good = new AttemptStore(path);
        good.RecordFailure("444904", Now);
        await good.SaveAsync(CancellationToken.None);
        var onDisk = await File.ReadAllTextAsync(path);

        var s = new AttemptStore(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await s.LoadAsync(CancellationToken.None);
            Assert.True(s.ShouldTry("444904", 7, Now)); // load failed -> empty history this run
        }

        s.RecordFailure("new-id", Now);
        await s.SaveAsync(CancellationToken.None);

        Assert.Equal(onDisk, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".invalid"));
    }

    [Fact]
    public async Task LoadOfEmptyFileIsNotAnError()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "");
        var s = new AttemptStore(path);
        await s.LoadAsync(CancellationToken.None);
        Assert.True(s.ShouldTry("anything", 7, Now));
    }

    /// <summary>C1 regression. The temp file must be closed and flushed *before* it is renamed
    /// over the real path, so the rename is the commit point. The original code called
    /// File.Move inside the `await using` scope, which renamed a still-buffered, zero-byte
    /// file — an observer therefore saw a published-but-empty attempts.json. Spins a reader
    /// against the destination while saving repeatedly; with the atomic pattern the file
    /// only ever appears complete, so a zero-length sighting can only mean an unflushed
    /// rename.</summary>
    [Fact]
    public async Task SaveNeverPublishesAnUnflushedFile()
    {
        var path = TempPath();
        var store = new AttemptStore(path);
        for (var i = 0; i < 50; i++) store.RecordFailure($"id-{i}", Now.AddDays(-i));

        var emptySightings = 0;
        using var stop = new CancellationTokenSource();
        var observer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length == 0) Interlocked.Increment(ref emptySightings);
                }
                catch (IOException)
                {
                    // Racing the writer; not what this test is measuring.
                }
            }
        });

        try
        {
            for (var i = 0; i < 200; i++)
            {
                await store.SaveAsync(CancellationToken.None);
                File.Delete(path);
            }
        }
        finally
        {
            stop.Cancel();
            await observer;
        }

        Assert.Equal(0, emptySightings);
    }

    [Fact]
    public async Task SaveOverwritesExistingStateFile()
    {
        var path = TempPath();
        var a = new AttemptStore(path);
        a.RecordFailure("first-id", Now);
        await a.SaveAsync(CancellationToken.None);

        var b = new AttemptStore(path);
        b.RecordFailure("second-id", Now);
        await b.SaveAsync(CancellationToken.None);

        var c = new AttemptStore(path);
        await c.LoadAsync(CancellationToken.None);
        Assert.True(c.ShouldTry("first-id", 7, Now));
        Assert.False(c.ShouldTry("second-id", 7, Now));
    }

    [Fact]
    public async Task VersionMigrationPreservesBackoffExceptForCorrectedMatcherIds()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var legacyPath = Path.Combine(directory, "attempts-v4.json");
        var currentPath = Path.Combine(directory, "attempts-v5.json");
        var legacy = new AttemptStore(legacyPath);
        legacy.RecordFailure("unchanged-miss", Now);
        legacy.RecordFailure("303067", Now);
        await legacy.SaveAsync(CancellationToken.None);

        var migrated = new AttemptStore(
            currentPath,
            legacyPath: legacyPath,
            excludedLegacyKeys: new HashSet<string> { "303067" });
        await migrated.LoadAsync(CancellationToken.None);

        Assert.False(migrated.ShouldTry("unchanged-miss", 7, Now));
        Assert.True(migrated.ShouldTry("303067", 7, Now));

        await migrated.SaveAsync(CancellationToken.None);
        Assert.True(File.Exists(currentPath));
        Assert.True(File.Exists(legacyPath));

        var reloaded = new AttemptStore(currentPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.False(reloaded.ShouldTry("unchanged-miss", 7, Now));
        Assert.True(reloaded.ShouldTry("303067", 7, Now));
    }
}
