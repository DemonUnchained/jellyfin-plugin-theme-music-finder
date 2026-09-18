using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class CompositeThemeProviderTests
{
    private static Series Series()
    {
        var series = new Series();
        series.SetProviderId(MetadataProvider.Tvdb, "tvdb");
        series.SetProviderId(MetadataProvider.Tmdb, "tmdb");
        return series;
    }

    private sealed class Provider(ThemeFetchResult result) : IThemeProvider
    {
        public int Calls { get; private set; }

        public Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ConfirmedPrimaryMissFallsThrough()
    {
        var primary = new Provider(ThemeFetchResult.NotFound("Plex 404"));
        var fallback = new Provider(ThemeFetchResult.Found([1, 2, 3]));
        var chain = new CompositeThemeProvider(primary, fallback);

        var result = await chain.FetchAsync(Series(), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task PrimaryTransientFailureStopsTheChain()
    {
        var primary = new Provider(ThemeFetchResult.Transient("Plex 503"));
        var fallback = new Provider(ThemeFetchResult.Found([1, 2, 3]));
        var chain = new CompositeThemeProvider(primary, fallback);

        var result = await chain.FetchAsync(Series(), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Transient, result.Status);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task UnusableCandidateFallsThroughToNextCuratedProvider()
    {
        var primary = new Provider(ThemeFetchResult.CandidateUnavailable("deleted video"));
        var fallback = new Provider(ThemeFetchResult.Found([1, 2, 3]));
        var chain = new CompositeThemeProvider(primary, fallback);

        var result = await chain.FetchAsync(Series(), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task UnusableCandidateAndFallbackMissBecomeConfirmedMiss()
    {
        var primary = new Provider(ThemeFetchResult.CandidateUnavailable("deleted video"));
        var fallback = new Provider(ThemeFetchResult.NotFound("Plex 404"));

        var result = await new CompositeThemeProvider(primary, fallback)
            .FetchAsync(Series(), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Contains("deleted video", result.Reason);
        Assert.Contains("Plex 404", result.Reason);
    }

    [Fact]
    public async Task PrimarySuccessDoesNotCallFallback()
    {
        var primary = new Provider(ThemeFetchResult.Found([1, 2, 3]));
        var fallback = new Provider(ThemeFetchResult.Found([4, 5, 6]));
        var chain = new CompositeThemeProvider(primary, fallback);

        var result = await chain.FetchAsync(Series(), CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Body);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task NoApplicableProviderMeansNoRequest()
    {
        var first = new Provider(ThemeFetchResult.NotApplicable("no TVDB"));
        var second = new Provider(ThemeFetchResult.NotApplicable("no TMDB"));

        var result = await new CompositeThemeProvider(first, second)
            .FetchAsync(Series(), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotApplicable, result.Status);
    }
}
