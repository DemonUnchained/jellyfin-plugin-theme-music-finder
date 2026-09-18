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
    IMediaEncoder mediaEncoder,
    IThemeAudioProcessor? audioProcessor = null) : IThemeAudioDownloader
{
    private const long MaxSourceBytes = 50L * 1024 * 1024;
    private const long MaxMp3Bytes = 8L * 1024 * 1024;
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(60);

    public async Task<byte[]> DownloadMp3Async(Uri source, CancellationToken ct)
    {
        var workDir = Path.Combine(appPaths.TempDirectory, "ThemeMusicFinder");
        Directory.CreateDirectory(workDir);
        var stem = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, stem + ".audio");
        var outputPath = Path.Combine(workDir, stem + ".mp3");

        try
        {
            // YoutubeExplode's default HttpClient has a 100-second whole-request timeout. In
            // practice a throttled/dead CDN response held the entire sequential sweep for that
            // full interval, seven times in one run. Give the complete candidate a bounded
            // budget and let the provider chain move on when it expires.
            using var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            candidateCts.CancelAfter(CandidateTimeout);
            using var youtubeHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var youtube = new YoutubeClient(youtubeHttp);

            YoutubeExplode.Videos.Streams.StreamManifest manifest;
            try
            {
                manifest = await youtube.Videos.Streams
                    .GetManifestAsync(source.ToString(), candidateCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"YouTube candidate exceeded the {CandidateTimeout.TotalSeconds:0}-second time limit.");
            }

            var audio = manifest.GetAudioOnlyStreams().ToList();
            var mp4 = audio.Where(stream => stream.Container == Container.Mp4).ToList();
            IStreamInfo? selected = null;
            if (mp4.Count > 0)
            {
                // The smallest AAC stream is normally still around 128 kbps and is much less
                // likely to trip YouTube's throttling than requesting the largest representation.
                selected = mp4.OrderBy(stream => stream.Size.Bytes).First();
            }
            else if (audio.Count > 0)
            {
                selected = audio.OrderBy(stream => stream.Size.Bytes).First();
            }
            else
            {
                // Some videos expose no separate audio-only representation. A small muxed stream
                // is safe because FFmpeg maps only 0:a:0, and the same 50 MiB cap still applies.
                selected = manifest.GetMuxedStreams()
                    .Where(stream => stream.Size.Bytes <= MaxSourceBytes)
                    .OrderBy(stream => stream.Size.Bytes)
                    .FirstOrDefault();
            }

            if (selected is null)
            {
                throw new InvalidOperationException("YouTube exposed no usable audio stream.");
            }

            if (selected.Size.Bytes > MaxSourceBytes)
            {
                throw new InvalidOperationException("YouTube audio stream exceeds the 50 MiB safety limit.");
            }

            try
            {
                await youtube.Videos.Streams
                    .DownloadAsync(selected, inputPath, progress: null, candidateCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"YouTube candidate exceeded the {CandidateTimeout.TotalSeconds:0}-second time limit.");
            }

            if (audioProcessor is not null)
            {
                var sourceBytes = await File.ReadAllBytesAsync(inputPath, ct).ConfigureAwait(false);
                var extension = selected.Container == Container.Mp4 ? ".m4a" : ".webm";
                return await audioProcessor
                    .ConvertToNormalizedMp3Async(sourceBytes, extension, ct)
                    .ConfigureAwait(false);
            }

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
