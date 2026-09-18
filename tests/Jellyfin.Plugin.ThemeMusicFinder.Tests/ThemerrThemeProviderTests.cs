using System.Net;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class ThemerrThemeProviderTests
{
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static Series Series(string? tmdbId)
    {
        var series = new Series();
        if (tmdbId is not null) series.SetProviderId(MetadataProvider.Tmdb, tmdbId);
        return series;
    }

    private static byte[] Mp3()
    {
        var bytes = new byte[400_000];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'3';
        return bytes;
    }

    [Fact]
    public async Task UsesTmdbIdAndDownloadsCuratedYoutubeUrl()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            "{\"youtube_theme_url\":\"https://www.youtube.com/watch?v=abc12345678\"}");
        var downloader = new FakeThemeAudioDownloader(_ => Mp3());
        var provider = new ThemerrThemeProvider(new HttpClient(handler), downloader);

        var result = await provider.FetchAsync(Series("1399"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("https://app.lizardbyte.dev/ThemerrDB/tv_shows/themoviedb/1399.json", handler.LastUri!.ToString());
        Assert.Equal("https://www.youtube.com/watch?v=abc12345678", Assert.Single(downloader.Requested).ToString());
    }

    [Fact]
    public async Task ReportsNotFoundForMissingDatabaseEntry()
    {
        var provider = new ThemerrThemeProvider(
            new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}")),
            new FakeThemeAudioDownloader(_ => Mp3()));

        var result = await provider.FetchAsync(Series("999999"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task MissingTmdbIdDoesNotMakeARequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var provider = new ThemerrThemeProvider(
            new HttpClient(handler),
            new FakeThemeAudioDownloader(_ => Mp3()));

        var result = await provider.FetchAsync(Series(null), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotApplicable, result.Status);
        Assert.Null(handler.LastUri);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=abc12345678")]
    [InlineData("https://youtube.example.com/watch?v=abc12345678")]
    [InlineData("https://example.com/theme.mp3")]
    public async Task RejectsUntrustedThemeUrls(string url)
    {
        var downloader = new FakeThemeAudioDownloader(_ => Mp3());
        var provider = new ThemerrThemeProvider(
            new HttpClient(new StubHandler(
                HttpStatusCode.OK,
                $"{{\"youtube_theme_url\":\"{url}\"}}")),
            downloader);

        var result = await provider.FetchAsync(Series("1399"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Empty(downloader.Requested);
    }

    [Fact]
    public async Task DownloaderFailureMarksOnlyTheCandidateUnavailable()
    {
        var provider = new ThemerrThemeProvider(
            new HttpClient(new StubHandler(
                HttpStatusCode.OK,
                "{\"youtube_theme_url\":\"https://youtu.be/abc12345678\"}")),
            new FakeThemeAudioDownloader(_ => throw new IOException("disk full")));

        var result = await provider.FetchAsync(Series("1399"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.CandidateUnavailable, result.Status);
        Assert.Contains("disk full", result.Reason);
    }

    [Fact]
    public void ClientFactoryUsesRequestLimits()
    {
        using var client = ThemerrThemeProvider.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.Equal(2L * 1024 * 1024, client.MaxResponseContentBufferSize);
    }
}
