namespace Jellyfin.Plugin.ThemeMusicFinder;

public interface IThemeAudioProcessor
{
    Task<byte[]> ConvertToNormalizedMp3Async(
        byte[] source,
        string sourceExtension,
        CancellationToken ct);
}
