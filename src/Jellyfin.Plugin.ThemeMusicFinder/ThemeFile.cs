namespace Jellyfin.Plugin.ThemeMusicFinder;

public static class ThemeFile
{
    /// <summary>Smallest plausible theme. The smallest real one observed was 447 KB;
    /// the upstream's 404 page is 467 bytes.</summary>
    private const int MinimumBytes = 32 * 1024;

    public static bool IsValidMp3(byte[] body, string? contentType)
    {
        if (body.Length < MinimumBytes) return false;
        if (contentType is null || !contentType.Contains("audio/mpeg", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasId3 = body[0] == 'I' && body[1] == 'D' && body[2] == '3';
        var hasFrameSync = body[0] == 0xFF && (body[1] & 0xE0) == 0xE0;
        return hasId3 || hasFrameSync;
    }

    /// <summary>Write via a temp file then move, so a process killed mid-write
    /// cannot leave a truncated file that later looks like a valid theme.</summary>
    public static async Task WriteAtomicAsync(string destPath, byte[] body, CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, body, ct).ConfigureAwait(false);
            File.Move(tmp, destPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
