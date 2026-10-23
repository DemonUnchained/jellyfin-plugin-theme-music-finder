using System.Text.Json;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class AutonomousThemeDiscoveryProviderTests
{
    [Fact]
    public void ParsesYoutubeSearchAndBuildsVerifiedCanonicalUrl()
    {
        var json = """
            {"entries":[{"id":"abc123_XYZ9","title":"The Enfield Poltergeist 2023 main theme",
            "channel":"Original Network","duration":80},{"id":"not-valid","title":"fake"}]}
            """;
        var hits = AutonomousThemeDiscoveryProvider.ParseSearchOutput(json, "YouTube");
        Assert.Single(hits);
        Assert.Equal("https://www.youtube.com/watch?v=abc123_XYZ9", hits[0].Url!.ToString());
        Assert.Equal(80, hits[0].Duration!.Value.TotalSeconds);
    }

    [Fact]
    public void ParsesAdditionalSoundCloudSource()
    {
        var json = """
            {"entries":[{"id":"12345","title":"The Enfield Poltergeist (2023) TV theme",
            "webpage_url":"https://soundcloud.com/composer/the-enfield-poltergeist",
            "uploader":"Composer","duration":95}]}
            """;
        var hits = AutonomousThemeDiscoveryProvider.ParseSearchOutput(json, "SoundCloud");
        Assert.Single(hits);
        Assert.Equal("SoundCloud", hits[0].Source);
        Assert.Equal("https://soundcloud.com/composer/the-enfield-poltergeist", hits[0].Url!.ToString());
    }

    [Fact]
    public void ParsesAnimeBilibiliByCanonicalBvId()
    {
        var json = """
            {"entries":[{"id":"BV1xx411c7mD","title":"Anime opening credits",
            "duration":90}]}
            """;
        var hits = AutonomousThemeDiscoveryProvider.ParseSearchOutput(json, "Bilibili");
        Assert.Single(hits);
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD", hits[0].Url!.ToString());
    }

    [Theory]
    [InlineData("https://soundcloud.com.evil.example/artist/track")]
    [InlineData("http://soundcloud.com/artist/track")]
    [InlineData("https://evil.example/artist/track")]
    [InlineData("https://soundcloud.com:8443/artist/track")]
    [InlineData("https://attacker@soundcloud.com/artist/track")]
    public void RejectsUntrustedSoundCloudUrls(string url)
    {
        Assert.False(AutonomousThemeDiscoveryProvider.IsAllowedUri("SoundCloud", new Uri(url)));
    }

    [Fact]
    public void DoesNotAcceptCrossSourceRedirect()
    {
        Assert.False(AutonomousThemeDiscoveryProvider.IsAllowedUri(
            "Bilibili", new Uri("https://www.youtube.com/watch?v=abc123_XYZ9")));
    }

    [Fact]
    public void SearchIgnoresUntrustedUrls()
    {
        var json = """
            {"entries":[{"id":"12345","title":"Real-sounding theme",
            "webpage_url":"https://soundcloud.com.evil.example/artist/track"}]}
            """;
        Assert.Empty(AutonomousThemeDiscoveryProvider.ParseSearchOutput(json, "SoundCloud"));
    }

    [Fact]
    public void CandidateCannotBeAcceptedOnSearchPositionAlone()
    {
        var video = new ThemeSearchVideo("abc123_XYZ9",
            "The Enfield Poltergeist (2023) - Music Video (cover)",
            "Official-looking account", TimeSpan.FromSeconds(90), SearchHits: 3);
        Assert.False(ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023, video).Accepted);
    }
}
