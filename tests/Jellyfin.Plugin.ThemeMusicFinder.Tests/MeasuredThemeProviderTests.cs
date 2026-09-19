using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class MeasuredThemeProviderTests
{
    [Fact]
    public async Task CountsOutcomesAndEmitsOneSummaryLine()
    {
        var series = new Series { Name = "Example" };
        series.SetProviderId(MetadataProvider.Tvdb, "1");
        var measured = new MeasuredThemeProvider(
            "TestProvider",
            new FakeThemeProvider(_ => ThemeFetchResult.NotFound("404")));

        await measured.FetchAsync(series, CancellationToken.None);
        await measured.FetchAsync(series, CancellationToken.None);
        var logger = new RecordingLogger<MeasuredThemeProvider>();
        measured.Log(logger);

        Assert.Equal(2, measured.CallCount);
        var entry = Assert.Single(logger.AtLevel(LogLevel.Information));
        Assert.Contains("TestProvider: 2 call(s)", entry.Message, StringComparison.Ordinal);
        Assert.Contains("2 not found", entry.Message, StringComparison.Ordinal);
    }
}
