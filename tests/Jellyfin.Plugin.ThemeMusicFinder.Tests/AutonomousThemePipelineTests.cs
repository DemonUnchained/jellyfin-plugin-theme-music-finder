using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

/// <summary>
/// Exercises the complete autonomous discovery -> detailed inspection -> confidence gate ->
/// downloader pipeline without network traffic. The real source command line is verified
/// separately by CI; injecting synthetic provider responses makes wrong-theme regressions
/// reproducible and allows asserting that rejected candidates never reach a downloader.
/// </summary>
public sealed class AutonomousThemePipelineTests
{
    private static Series Show()
        => new() { Name = "The Enfield Poltergeist", ProductionYear = 2023 };

    private static ThemeSearchVideo Candidate(string id, string title, string source = "YouTube")
        => new(id, title, "TV Network", TimeSpan.FromSeconds(90),
            Source: source,
            Url: source == "YouTube"
                ? new Uri("https://www.youtube.com/watch?v=" + id)
                : new Uri("https://soundcloud.com/studio/" + id));

    private static byte[] Mp3()
    {
        var bytes = new byte[40_000];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = (byte)'3';
        return bytes;
    }

    private static string Marker()
    {
        var path = Path.GetTempFileName();
        return path;
    }

    [Fact]
    public async Task AuditOnlyReturnsWinnerWithoutDownloadingAudio()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ =>
                throw new InvalidOperationException("Audit must never call a downloader"));
            var selected = Candidate("abc123_XYZ9",
                "The Enfield Poltergeist (2023) TV main title theme");

            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker, auditOnly: true)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (_, _, _) =>
                    Task.FromResult<IReadOnlyList<ThemeSearchVideo>>([selected]),
                InspectOverride = (video, _) => Task.FromResult(video)
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.Preview, result.Status);
            Assert.Null(result.Body);
            Assert.Equal(selected.Url, result.SourceUri);
            Assert.Equal(selected.Title, result.CandidateTitle);
            Assert.True(result.ConfidenceScore >= ThemeCandidateDecisionEngine.MinimumAutomaticScore);
            Assert.Empty(downloader.Requested);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task AuditOnlyDoesNotDownloadAmbiguousMatches()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ =>
                throw new InvalidOperationException("Never download in audit mode"));
            var a = Candidate("abc123_XYZ9",
                "The Enfield Poltergeist (2023) TV main title theme");
            var b = Candidate("abc123_XYZ8",
                "The Enfield Poltergeist (2023) TV main title theme");
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker, auditOnly: true)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (_, _, _) =>
                    Task.FromResult<IReadOnlyList<ThemeSearchVideo>>([a, b]),
                InspectOverride = (video, _) => Task.FromResult(video)
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
            Assert.Contains("competing", result.Reason);
            Assert.Empty(downloader.Requested);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task VerifiedSingleWinnerDownloadsOnce()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ => Mp3());
            var found = Candidate("abc123_XYZ9", "The Enfield Poltergeist (2023) TV main title theme");
            var searched = new List<string>();
            var inspected = 0;
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (source, query, _) =>
                {
                    searched.Add(source + "|" + query);
                    return Task.FromResult<IReadOnlyList<ThemeSearchVideo>>([found]);
                },
                InspectOverride = (video, _) =>
                {
                    inspected++;
                    return Task.FromResult(video with { Duration = TimeSpan.FromSeconds(91) });
                }
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);

            Assert.Equal(ThemeFetchStatus.Found, result.Status);
            Assert.Equal("https://www.youtube.com/watch?v=abc123_XYZ9", result.SourceUri?.ToString());
            Assert.Single(downloader.Requested);
            Assert.Single(searched.Select(s => s.Split('|')[0]).Distinct());
            Assert.Equal(3, searched.Count);
            Assert.Equal(1, inspected);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task InspectionThatRevealsWrongYearNeverDownloads()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ => Mp3());
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (_, _, _) => Task.FromResult<IReadOnlyList<ThemeSearchVideo>>(
                    [Candidate("abc123_XYZ9", "The Enfield Poltergeist (2023) TV main title theme")]),
                InspectOverride = (video, _) => Task.FromResult(video with {
                    Title = "The Enfield Poltergeist (2015) official TV main title theme"
                })
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
            Assert.Empty(downloader.Requested);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task CompetingTitlesDoNotTriggerArbitraryDownload()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ => Mp3());
            var a = Candidate("abc123_XYZ9", "The Enfield Poltergeist (2023) TV main title theme");
            var b = Candidate("abc123_XYZ8", "The Enfield Poltergeist (2023) TV main title theme");
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (_, _, _) => Task.FromResult<IReadOnlyList<ThemeSearchVideo>>([a, b]),
                InspectOverride = (video, _) => Task.FromResult(video)
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
            Assert.Contains("competing", result.Reason);
            Assert.Empty(downloader.Requested);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task UnavailableSourceCannotBecomeSevenDayNegativeCache()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ => Mp3());
            var searches = 0;
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: false, enableBilibili: false,
                executablePathOverride: marker)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (_, _, _) =>
                {
                    searches++;
                    throw new InvalidOperationException("YouTube HTTP 429");
                },
                InspectOverride = (video, _) => Task.FromResult(video)
            };

            var first = await provider.FetchAsync(Show(), CancellationToken.None);
            var second = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.Transient, first.Status);
            Assert.Equal(ThemeFetchStatus.Transient, second.Status);
            Assert.Equal(1, searches); // open circuit avoids repeated provider hammering
            Assert.Empty(downloader.Requested);
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task SoundCloudCanWinAfterYoutubeReturnsNoResults()
    {
        var marker = Marker();
        try
        {
            var downloader = new FakeThemeAudioDownloader(_ => Mp3());
            var track = Candidate("original-opening", "The Enfield Poltergeist (2023) TV main title theme", "SoundCloud");
            var provider = new AutonomousThemeDiscoveryProvider(
                null!, downloader, enableSoundCloud: true, enableBilibili: false,
                executablePathOverride: marker)
            {
                DelayAsync = (_, _) => Task.CompletedTask,
                SearchOverride = (source, _, _) => Task.FromResult<IReadOnlyList<ThemeSearchVideo>>(
                    source == "SoundCloud" ? [track] : []),
                InspectOverride = (video, _) => Task.FromResult(video)
            };

            var result = await provider.FetchAsync(Show(), CancellationToken.None);
            Assert.Equal(ThemeFetchStatus.Found, result.Status);
            Assert.Contains("SoundCloud", result.Source);
            Assert.Single(downloader.Requested);
            Assert.Equal(track.Url, downloader.Requested[0]);
        }
        finally { File.Delete(marker); }
    }
}
