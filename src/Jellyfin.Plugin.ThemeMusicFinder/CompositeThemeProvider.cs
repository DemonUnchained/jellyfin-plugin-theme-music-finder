using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Runs trusted providers in priority order. A provider-wide transient failure stops
/// the chain, but one unusable curated candidate falls through to the next provider.</summary>
public sealed class CompositeThemeProvider(params IThemeProvider[] providers) : IThemeProvider
{
    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var misses = new List<string>();
        var madeRequest = false;

        foreach (var provider in providers)
        {
            var result = await provider.FetchAsync(series, ct).ConfigureAwait(false);
            switch (result.Status)
            {
                case ThemeFetchStatus.Found:
                case ThemeFetchStatus.Transient:
                    return result;
                case ThemeFetchStatus.NotFound:
                    madeRequest = true;
                    misses.Add(result.Reason ?? "not found");
                    break;
                case ThemeFetchStatus.CandidateUnavailable:
                    madeRequest = true;
                    misses.Add(result.Reason ?? "candidate unavailable");
                    break;
                case ThemeFetchStatus.NotApplicable:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown theme-fetch status {result.Status}.");
            }
        }

        return madeRequest
            ? ThemeFetchResult.NotFound(string.Join("; ", misses))
            : ThemeFetchResult.NotApplicable("no enabled provider has a usable series identifier");
    }
}
