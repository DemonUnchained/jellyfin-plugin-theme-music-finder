using System.Net;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public class PlexThemeProviderTests
{
    private sealed class StubHandler(HttpStatusCode code, byte[] body, string contentType)
        : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(code) { Content = content });
        }
    }

    private static byte[] FakeMp3(int size = 400_000)
    {
        var b = new byte[size];
        b[0] = (byte)'I'; b[1] = (byte)'D'; b[2] = (byte)'3';
        return b;
    }

    private static PlexThemeProvider Provider(HttpStatusCode code, byte[] body, string contentType)
        => new(new HttpClient(new StubHandler(code, body, contentType)));

    private static Series Series(string? tvdbId)
    {
        var series = new Series();
        if (tvdbId is not null) series.SetProviderId(MetadataProvider.Tvdb, tvdbId);
        return series;
    }

    [Fact]
    public async Task ReturnsBytesForValidMp3()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakeMp3(), "audio/mpeg");
        var provider = new PlexThemeProvider(new HttpClient(handler));

        var result = await provider.FetchAsync(Series("371572"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.NotNull(result.Body);
        Assert.Equal("https://tvthemes.plexapp.com/371572.mp3", handler.LastUri!.ToString());
    }

    /// <summary>C2. 404 is the one answer that means "there is no theme for this series", and
    /// the only one that may earn a multi-day backoff.</summary>
    [Fact]
    public async Task ReportsNotFoundOn404()
    {
        var html = System.Text.Encoding.ASCII.GetBytes("<html>404</html>");
        var result = await Provider(HttpStatusCode.NotFound, html, "text/html")
            .FetchAsync(Series("444904"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Null(result.Body);
    }

    /// <summary>C2. A broken or rate-limiting upstream says nothing about coverage. Recording
    /// these as failures is what turns one bad hour into a week of a silently themeless library.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ReportsTransientForUpstreamFailureStatuses(HttpStatusCode code)
    {
        var result = await Provider(code, [], "text/html")
            .FetchAsync(Series("444904"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Transient, result.Status);
        Assert.Null(result.Body);
        Assert.Contains(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Reason);
    }

    /// <summary>C2. A truncated body is a failed download, not evidence that no theme exists.</summary>
    [Fact]
    public async Task ReportsTransientWhenBodyIsTruncated()
    {
        var result = await Provider(HttpStatusCode.OK, FakeMp3(1000), "audio/mpeg")
            .FetchAsync(Series("371572"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Transient, result.Status);
        Assert.Null(result.Body);
    }

    /// <summary>C2. A 200 carrying an error page or a captive-portal redirect is the upstream
    /// misbehaving, not a coverage gap.</summary>
    [Fact]
    public async Task ReportsTransientWhenBodyIsNotAudioDespite200()
    {
        var result = await Provider(HttpStatusCode.OK, FakeMp3(400_000), "text/html")
            .FetchAsync(Series("371572"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Transient, result.Status);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task EscapesUnexpectedCharactersInTvdbId()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakeMp3(), "audio/mpeg");
        var provider = new PlexThemeProvider(new HttpClient(handler));

        var result = await provider.FetchAsync(Series("12?x=1"), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("https://tvthemes.plexapp.com/12%3Fx%3D1.mp3", handler.LastUri!.ToString());
    }

    /// <summary>I6. The shared factory is the only construction site for the upstream client, so
    /// the timeout and the response cap cannot drift between the two trigger paths.</summary>
    [Fact]
    public void CreateClientIsHardenedAgainstAStalledOrOversizedResponse()
    {
        using var http = PlexThemeProvider.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
        Assert.Equal(8L * 1024 * 1024, http.MaxResponseContentBufferSize);
        Assert.Contains("Jellyfin-ThemeMusicFinder", http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task MissingTvdbIdDoesNotMakeARequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, FakeMp3(), "audio/mpeg");
        var provider = new PlexThemeProvider(new HttpClient(handler));

        var result = await provider.FetchAsync(Series(null), CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotApplicable, result.Status);
        Assert.Null(handler.LastUri);
    }
}
