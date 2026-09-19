namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class YoutubeThemeAudioDownloaderTests
{
    [Fact]
    public void YtDlpInvocationReusesConfigAndRequestsBoundedAudioOnlyDownload()
    {
        var source = new Uri("https://www.youtube.com/watch?v=V9irhnypAK8");
        var startInfo = YoutubeThemeAudioDownloader.CreateYtDlpStartInfo(
            "/config/trailer-tools/yt-dlp",
            "/config/trailer-tools/yt-dlp.conf",
            source,
            "/cache/theme.%(ext)s");

        Assert.Equal("/config/trailer-tools/yt-dlp", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            [
                "--config-locations", "/config/trailer-tools/yt-dlp.conf",
                "--no-playlist",
                "--no-progress",
                "--socket-timeout", "20",
                "--retries", "3",
                "--fragment-retries", "3",
                "--extractor-retries", "3",
                "--format", "ba[ext=m4a]/ba",
                "--max-filesize", "50M",
                "--output", "/cache/theme.%(ext)s",
                source.ToString()
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public void YtDlpInvocationDoesNotInventAMissingConfigFile()
    {
        var startInfo = YoutubeThemeAudioDownloader.CreateYtDlpStartInfo(
            "/tools/yt-dlp",
            null,
            new Uri("https://youtu.be/example"),
            "/tmp/theme.%(ext)s");

        Assert.DoesNotContain("--config-locations", startInfo.ArgumentList);
    }
}
