using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Avoids sending known live-action TV libraries to the anime-only catalogue. If
/// Jellyfin cannot yet identify a collection folder (common during item-added events), it falls
/// through to the inner provider so the optimization can never become a false negative.</summary>
internal sealed class AnimeLibraryThemeProvider(
    ILibraryManager libraryManager,
    IThemeProvider inner) : IThemeProvider
{
    internal int SkippedSeriesCount { get; private set; }

    public Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        List<Folder> folders;
        try
        {
            folders = libraryManager.GetCollectionFolders(series);
        }
        catch
        {
            // Applicability detection is only an optimization. Never turn a library-manager
            // lookup problem into a missed theme or a failed sweep.
            return inner.FetchAsync(series, ct);
        }

        if (folders.Count == 0 || IsLikelyAnime(series, folders))
            return inner.FetchAsync(series, ct);

        SkippedSeriesCount++;
        return Task.FromResult(ThemeFetchResult.NotApplicable(
            "AnimeThemes skipped a non-animation series outside an anime-named library"));
    }

    internal static bool IsLikelyAnime(Series series, IEnumerable<Folder> collectionFolders)
    {
        if (series.Genres.Any(genre =>
                string.Equals(genre, "Animation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(genre, "Anime", StringComparison.OrdinalIgnoreCase)))
            return true;

        return collectionFolders.Any(folder =>
            ContainsAnime(folder.Name)
            || ContainsAnime(folder.Path));
    }

    private static bool ContainsAnime(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains("anime", StringComparison.OrdinalIgnoreCase);
}
