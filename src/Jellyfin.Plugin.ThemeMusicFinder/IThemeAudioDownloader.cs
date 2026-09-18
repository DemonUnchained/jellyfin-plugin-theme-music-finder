namespace Jellyfin.Plugin.ThemeMusicFinder;

internal interface IThemeAudioDownloader
{
    Task<byte[]> DownloadMp3Async(Uri source, CancellationToken ct);
}
