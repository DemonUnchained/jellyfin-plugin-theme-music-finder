using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Highest-priority local source for an administrator-selected video bound to a stable
/// catalogue ID. It performs no search and therefore cannot confuse similarly named series.</summary>
internal sealed class CuratedThemeSourceProvider(
    CuratedThemeSourceStore store,
    IThemeAudioDownloader downloader) : IThemeProvider
{
    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        if (!store.TryGetSource(series, out var source))
        {
            return ThemeFetchResult.NotApplicable(
                "no curated stable-ID theme-source override");
        }

        try
        {
            var body = await downloader.DownloadMp3Async(source, ct).ConfigureAwait(false);
            return ThemeFile.IsValidMp3(body, "audio/mpeg")
                ? ThemeFetchResult.Found(body, "Curated source override", source)
                : ThemeFetchResult.CandidateUnavailable(
                    "Curated source override did not produce a valid MP3");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A manually selected video can be removed, geo-blocked, or temporarily unavailable.
            // Let public providers continue, but keep the overall miss retryable so fixing the
            // configured URL does not have to wait out a newly written negative cache.
            return ThemeFetchResult.CandidateUnavailable(
                $"Curated source override was unusable: {ex.Message}");
        }
    }
}
