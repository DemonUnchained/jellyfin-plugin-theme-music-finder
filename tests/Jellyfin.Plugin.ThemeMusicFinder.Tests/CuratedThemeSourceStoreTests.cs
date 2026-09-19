using System.Text.Json;
using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class CuratedThemeSourceStoreTests
{
    private static string TempPath() => Path.Combine(
        Directory.CreateTempSubdirectory("theme-sources-").FullName,
        "sources.json");

    [Fact]
    public async Task MissingFileIsCreatedEmpty()
    {
        var path = TempPath();

        var store = await CuratedThemeSourceStore.LoadOrCreateAsync(
            path,
            new RecordingLogger<CuratedThemeSourceStore>(),
            CancellationToken.None);

        Assert.Empty(store.Mappings);
        Assert.True(File.Exists(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Empty(json.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task StableIdSelectsAndNormalizesOneYoutubeVideo()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, """
            {
              "tvdb:407633": "https://youtu.be/abc12345678?si=tracking",
              "tmdb:75475": "https://www.youtube.com/watch?v=zyx98765432&t=10"
            }
            """);
        var store = await CuratedThemeSourceStore.LoadOrCreateAsync(
            path,
            new RecordingLogger<CuratedThemeSourceStore>(),
            CancellationToken.None);
        var series = new Series { Name = "When They Cry" };
        series.SetProviderId(MetadataProvider.Tvdb, "407633");
        series.SetProviderId(MetadataProvider.Tmdb, "75475");

        Assert.True(store.TryGetSource(series, out var source));
        Assert.Equal("https://www.youtube.com/watch?v=abc12345678", source.AbsoluteUri);
        Assert.StartsWith("source-override-", store.GetCacheSuffix(series), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidKeysHostsAndNonVideoUrlsAreIgnored()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, """
            {
              "name:unsafe": "https://www.youtube.com/watch?v=abc12345678",
              "tvdb:1": "https://example.com/watch?v=abc12345678",
              "tvdb:2": "https://www.youtube.com/playlist?list=abc12345678",
              "tmdb:3": "http://www.youtube.com/watch?v=abc12345678"
            }
            """);
        var logger = new RecordingLogger<CuratedThemeSourceStore>();

        var store = await CuratedThemeSourceStore.LoadOrCreateAsync(
            path,
            logger,
            CancellationToken.None);

        Assert.Empty(store.Mappings);
        Assert.Contains(
            logger.AtLevel(LogLevel.Warning),
            entry => entry.Message.Contains("Ignored 4 invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void EditingUrlChangesOnlyTheAffectedCacheSuffix()
    {
        var first = new CuratedThemeSourceStore(new Dictionary<string, Uri>
        {
            ["tvdb:1"] = new("https://www.youtube.com/watch?v=abc12345678")
        });
        var changed = new CuratedThemeSourceStore(new Dictionary<string, Uri>
        {
            ["tvdb:1"] = new("https://www.youtube.com/watch?v=zyx98765432")
        });
        var series = new Series();
        series.SetProviderId(MetadataProvider.Tvdb, "1");

        Assert.NotEqual(first.GetCacheSuffix(series), changed.GetCacheSuffix(series));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc12345678", "https://www.youtube.com/watch?v=abc12345678")]
    [InlineData("https://m.youtube.com/watch?v=abc12345678&list=ignored", "https://www.youtube.com/watch?v=abc12345678")]
    [InlineData("https://www.youtube.com/shorts/abc12345678", "https://www.youtube.com/watch?v=abc12345678")]
    [InlineData("https://youtu.be/abc12345678?t=20", "https://www.youtube.com/watch?v=abc12345678")]
    public void SupportedYoutubeLinksNormalizeToCanonicalWatchUrl(
        string input,
        string expected)
    {
        Assert.True(CuratedThemeSourceStore.TryNormalizeYoutubeVideoUri(input, out var source));
        Assert.Equal(expected, source.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc%201234567")]
    public void EscapedOrMalformedVideoIdsAreRejected(string input)
        => Assert.False(CuratedThemeSourceStore.TryNormalizeYoutubeVideoUri(input, out _));
}
