using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Uses Jellyfin's FFmpeg to produce a client-compatible MP3 and, when enabled,
/// normalize it to a predictable EBU R128 loudness.</summary>
internal sealed class FfmpegThemeAudioProcessor(
    IApplicationPaths appPaths,
    IMediaEncoder mediaEncoder,
    bool normalize,
    double targetLufs) : IThemeAudioProcessor
{
    private const long MaxSourceBytes = 50L * 1024 * 1024;
    private const long MaxMp3Bytes = 8L * 1024 * 1024;

    public async Task<byte[]> ConvertToNormalizedMp3Async(
        byte[] source,
        string sourceExtension,
        CancellationToken ct)
    {
        if (source.Length == 0 || source.LongLength > MaxSourceBytes)
        {
            throw new InvalidOperationException("Theme audio is empty or exceeds the 50 MiB safety limit.");
        }

        if (string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath))
        {
            throw new InvalidOperationException("Jellyfin did not provide an FFmpeg path.");
        }

        var safeExtension = sourceExtension is ".ogg" or ".webm" or ".m4a" or ".mp3"
            ? sourceExtension
            : ".audio";
        var workDir = Path.Combine(appPaths.TempDirectory, "ThemeMusicFinder");
        Directory.CreateDirectory(workDir);
        var stem = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, stem + safeExtension);
        var outputPath = Path.Combine(workDir, stem + ".mp3");

        try
        {
            await File.WriteAllBytesAsync(inputPath, source, ct).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo
            {
                FileName = mediaEncoder.EncoderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", inputPath, "-map", "0:a:0", "-vn"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (normalize)
            {
                startInfo.ArgumentList.Add("-af");
                startInfo.ArgumentList.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "loudnorm=I={0:0.0}:LRA=11:TP=-1.5",
                    targetLufs));
            }

            foreach (var argument in new[] { "-codec:a", "libmp3lame", "-q:a", "2", outputPath })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("FFmpeg could not be started.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg exited with code {process.ExitCode}: {Truncate(stderr, 500)}");
            }

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxMp3Bytes)
            {
                throw new InvalidOperationException("Converted MP3 is empty or exceeds the 8 MiB safety limit.");
            }

            return await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
