using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class AnimeLibraryThemeProviderTests
{
    private static (AnimeLibraryThemeProvider Scoped, FakeThemeProvider Inner) Build(
        Series series,
        params Folder[] folders)
    {
        var library = new FakeLibraryManager(series)
        {
            CollectionFolders = _ => [.. folders]
        };
        var inner = new FakeThemeProvider(_ => ThemeFetchResult.NotFound("not found"));
        return (new AnimeLibraryThemeProvider(library, inner), inner);
    }

    [Fact]
    public async Task KnownLiveActionLibraryIsSkippedBeforeAnimeThemesHttp()
    {
        var series = new Series { Name = "Live Action", Genres = ["Drama"] };
        series.SetProviderId(MetadataProvider.Tvdb, "1");
        var (scoped, inner) = Build(series, new Folder { Name = "TV", Path = "/media/tv" });

        var result = await scoped.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotApplicable, result.Status);
        Assert.Empty(inner.Requested);
        Assert.Equal(1, scoped.SkippedSeriesCount);
    }

    [Fact]
    public async Task AnimeNamedLibraryUsesAnimeThemesEvenForIncompleteGenres()
    {
        var series = new Series { Name = "Anime", Genres = [] };
        series.SetProviderId(MetadataProvider.Tvdb, "2");
        var (scoped, inner) = Build(
            series,
            new Folder { Name = "Anime TV", Path = "/media/anime-tv" });

        var result = await scoped.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Single(inner.Requested);
    }

    [Fact]
    public async Task AnimationGenreUsesAnimeThemesOutsideAnimeNamedLibrary()
    {
        var series = new Series { Name = "Animated", Genres = ["Animation"] };
        series.SetProviderId(MetadataProvider.Tvdb, "3");
        var (scoped, inner) = Build(series, new Folder { Name = "TV", Path = "/media/tv" });

        await scoped.FetchAsync(series, CancellationToken.None);

        Assert.Single(inner.Requested);
    }

    [Fact]
    public async Task UnknownCollectionFolderFallsThroughInsteadOfCreatingAFalseNegative()
    {
        var series = new Series { Name = "Not Yet Attached", Genres = [] };
        series.SetProviderId(MetadataProvider.Tvdb, "4");
        var (scoped, inner) = Build(series);

        await scoped.FetchAsync(series, CancellationToken.None);

        Assert.Single(inner.Requested);
    }
}
