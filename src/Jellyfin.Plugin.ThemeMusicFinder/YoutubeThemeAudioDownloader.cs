using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Downloads the curated YouTube audio stream and uses Jellyfin's own FFmpeg binary to
/// create a real MP3. Naming an AAC/WebM stream theme.mp3 is not sufficient for every client.</summary>
internal sealed class YoutubeThemeAudioDownloader(
    IApplicationPaths appPaths,
    IMediaEncoder mediaEncoder) : IThemeAudioDownloader
{
    private const long MaxSourceBytes = 50L * 1024 * 1024;
    private const long MaxMp3Bytes = 8L * 1024 * 1024;

    public async Task<byte[]> DownloadMp3Async(Uri source, CancellationToken ct)
    {
        var workDir = Path.Combine(appPaths.TempDirectory, "ThemeMusicFinder");
        Directory.CreateDirectory(workDir);
        var stem = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, stem + ".audio");
        var outputPath = Path.Combine(workDir, stem + ".mp3");

        try
        {
            var youtube = new YoutubeClient();
            var manifest = await youtube.Videos.Streams
                .GetManifestAsync(source.ToString(), ct)
                .ConfigureAwait(false);
            var audio = manifest.GetAudioOnlyStreams().ToList();
            var mp4 = audio.Where(stream => stream.Container == Container.Mp4).ToList();
            IStreamInfo? selected = null;
            if (mp4.Count > 0)
            {
                selected = mp4.GetWithHighestBitrate();
            }
            else if (audio.Count > 0)
            {
                selected = audio.GetWithHighestBitrate();
            }

            if (selected is null)
            {
                throw new InvalidOperationException("YouTube exposed no audio-only stream.");
            }

            if (selected.Size.Bytes > MaxSourceBytes)
            {
                throw new InvalidOperationException("YouTube audio stream exceeds the 50 MiB safety limit.");
            }

            await youtube.Videos.Streams
                .DownloadAsync(selected, inputPath, progress: null, ct)
                .ConfigureAwait(false);
            await TranscodeAsync(inputPath, outputPath, ct).ConfigureAwait(false);

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

    private async Task TranscodeAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath))
        {
            throw new InvalidOperationException("Jellyfin did not provide an FFmpeg path.");
        }

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
            "-i", inputPath, "-map", "0:a:0", "-vn", "-codec:a", "libmp3lame",
            "-q:a", "2", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

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
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale temp file is preferable to hiding the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }
}
