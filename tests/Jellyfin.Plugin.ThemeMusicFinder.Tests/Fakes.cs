using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

// Hand-rolled fakes, deliberately in preference to a mocking library: ThemeDownloadService
// touches exactly two members across the two enormous Jellyfin interfaces, and the behaviour
// under test (call counts, ordering, "was this called at all") is easier to read as plain
// recording fields than as verify() incantations. The hundreds of members it never touches are
// mechanically generated NotImplementedException stubs in Fakes.Generated.cs, so anything the
// service starts calling by accident fails loudly instead of silently returning a default.

internal sealed partial class FakeLibraryManager(params BaseItem[] items)
{
    /// <summary>Every query the service issued, so a test can assert it asked for series
    /// recursively rather than trusting the returned list.</summary>
    public List<InternalItemsQuery> Queries { get; } = [];

    public Func<BaseItem, List<Folder>> CollectionFolders { get; set; } = _ => [];

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
    {
        Queries.Add(query);
        return items;
    }

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, bool allowExternalContent)
        => throw new NotSupportedException("ThemeDownloadService must not use this overload.");

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, List<BaseItem> parents)
        => throw new NotSupportedException("ThemeDownloadService must not use this overload.");

    public List<Folder> GetCollectionFolders(BaseItem item) => CollectionFolders(item);
}

internal sealed partial class FakeProviderManager
{
    /// <summary>Every item handed to RefreshSingleItem, in order.</summary>
    public List<BaseItem> Refreshed { get; } = [];

    /// <summary>Lets a test make the refresh itself blow up, which is one of the real-world
    /// ways a single series can fault mid-sweep.</summary>
    public Action<BaseItem>? OnRefresh { get; set; }

    public Task<ItemUpdateType> RefreshSingleItem(
        BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        Refreshed.Add(item);
        OnRefresh?.Invoke(item);
        return Task.FromResult(ItemUpdateType.None);
    }
}

internal sealed class FakeThemeProvider(Func<string, ThemeFetchResult> respond) : IThemeProvider
{
    /// <summary>Every tvdb id actually requested upstream, in order. An empty list is the
    /// assertion for "this series was skipped without a fetch".</summary>
    public List<string> Requested { get; } = [];

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var id = series.GetProviderId(MetadataProvider.Tvdb)
            ?? $"tmdb:{series.GetProviderId(MetadataProvider.Tmdb)}";
        Requested.Add(id);
        // Yield so a throwing responder surfaces as a faulted task rather than a synchronous
        // throw, matching how a real HttpClient failure reaches the caller.
        await Task.Yield();
        return respond(id);
    }
}

internal sealed class FakeThemeAudioDownloader(Func<Uri, byte[]> respond) : IThemeAudioDownloader
{
    public List<Uri> Requested { get; } = [];

    public Task<byte[]> DownloadMp3Async(Uri source, CancellationToken ct)
    {
        Requested.Add(source);
        return Task.FromResult(respond(source));
    }
}

internal sealed class FakeThemeAudioProcessor(Func<byte[], string, byte[]> respond) : IThemeAudioProcessor
{
    public List<(byte[] Body, string Extension)> Requested { get; } = [];

    public Task<byte[]> ConvertToNormalizedMp3Async(
        byte[] source,
        string sourceExtension,
        CancellationToken ct)
    {
        Requested.Add((source, sourceExtension));
        return Task.FromResult(respond(source, sourceExtension));
    }
}

/// <summary>Stands in for <see cref="Task.Delay(TimeSpan, CancellationToken)"/> so the throttle
/// can be asserted rather than slept through.</summary>
internal sealed class DelayRecorder
{
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>Runs when a delay is requested — a test uses it to cancel mid-sweep.</summary>
    public Action? OnDelay { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        OnDelay?.Invoke();
        // Task.Delay observes the token; a fake that ignored it would hide a cancellation bug.
        return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IEnumerable<LogEntry> AtLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
}
