using System.Text.Json;
using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class AnimeThemeTitleOverrideStoreTests
{
    [Fact]
    public async Task MissingFileIsCreatedWithVerifiedDefaults()
    {
        var path = Path.Combine(
            Directory.CreateTempSubdirectory("theme-overrides-").FullName,
            "overrides.json");
        var logger = new RecordingLogger<AnimeThemeTitleOverrideStore>();

        var store = await AnimeThemeTitleOverrideStore.LoadOrCreateAsync(
            path,
            logger,
            CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal("Norn9: Norn+Nonet", store.Mappings["tvdb:303067"][0]);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.True(json.RootElement.TryGetProperty("tmdb:254853", out _));
    }

    [Fact]
    public async Task CustomStableIdAliasChangesOnlyThatSeriesCacheSuffix()
    {
        var path = Path.Combine(
            Directory.CreateTempSubdirectory("theme-overrides-").FullName,
            "overrides.json");
        await File.WriteAllTextAsync(path, """
            {
              "tvdb:999": ["Exact Catalogue Alias"],
              "name:unsafe": ["Ignored"],
              "tvdb:-1": ["Ignored"]
            }
            """);
        var store = await AnimeThemeTitleOverrideStore.LoadOrCreateAsync(
            path,
            new RecordingLogger<AnimeThemeTitleOverrideStore>(),
            CancellationToken.None);
        var series = new Series { Name = "Local Name", ProductionYear = 2026 };
        series.SetProviderId(MetadataProvider.Tvdb, "999");

        var titles = AnimeThemesProvider.GetLookupTitles(series, store.Mappings);
        var firstSuffix = AnimeThemesProvider.GetOverrideCacheSuffix(series, store.Mappings);
        var changedSuffix = AnimeThemesProvider.GetOverrideCacheSuffix(
            series,
            new Dictionary<string, string[]> { ["tvdb:999"] = ["Changed Alias"] });

        Assert.Equal("Exact Catalogue Alias", titles[0]);
        Assert.NotNull(firstSuffix);
        Assert.NotEqual(firstSuffix, changedSuffix);
        Assert.Single(store.Mappings);
    }
}
