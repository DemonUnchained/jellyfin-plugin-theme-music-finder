using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class CuratedThemeSourceProviderTests
{
    private static byte[] Mp3()
    {
        var bytes = new byte[400_000];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'3';
        return bytes;
    }

    private static Series Series(string id)
    {
        var series = new Series { Name = "Example" };
        series.SetProviderId(MetadataProvider.Tvdb, id);
        return series;
    }

    [Fact]
    public async Task ExactStableIdDownloadsSelectedVideo()
    {
        var source = new Uri("https://www.youtube.com/watch?v=abc12345678");
        var store = new CuratedThemeSourceStore(new Dictionary<string, Uri>
        {
            ["tvdb:1"] = source
        });
        var downloader = new FakeThemeAudioDownloader(_ => Mp3());
        var provider = new CuratedThemeSourceProvider(store, downloader);

        var result = await provider.FetchAsync(Series("1"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("Curated source override", result.Source);
        Assert.Equal(source, result.SourceUri);
        Assert.Equal(source, Assert.Single(downloader.Requested));
    }

    [Fact]
    public async Task UnmappedSeriesIsNotApplicableAndMakesNoDownload()
    {
        var store = new CuratedThemeSourceStore(new Dictionary<string, Uri>());
        var downloader = new FakeThemeAudioDownloader(_ => Mp3());

        var result = await new CuratedThemeSourceProvider(store, downloader)
            .FetchAsync(Series("2"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotApplicable, result.Status);
        Assert.Empty(downloader.Requested);
    }

    [Fact]
    public async Task DeadSelectedVideoFallsThroughButRemainsRetryable()
    {
        var store = new CuratedThemeSourceStore(new Dictionary<string, Uri>
        {
            ["tvdb:1"] = new("https://www.youtube.com/watch?v=abc12345678")
        });
        var provider = new CuratedThemeSourceProvider(
            store,
            new FakeThemeAudioDownloader(_ => throw new IOException("private video")));

        var result = await provider.FetchAsync(Series("1"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.CandidateUnavailable, result.Status);
        Assert.Contains("private video", result.Reason, StringComparison.Ordinal);
    }
}
