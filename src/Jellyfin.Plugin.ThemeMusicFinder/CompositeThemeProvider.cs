using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Runs trusted providers in priority order. A temporary failure at one independent
/// provider does not prevent later providers from succeeding, but it is retained as the final
/// result when every later provider misses so the incomplete lookup never earns backoff.</summary>
public sealed class CompositeThemeProvider(params IThemeProvider[] providers) : IThemeProvider
{
    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var failures = new List<string>();
        var madeRequest = false;
        var hadTransientFailure = false;

        foreach (var provider in providers)
        {
            var result = await provider.FetchAsync(series, ct).ConfigureAwait(false);
            switch (result.Status)
            {
                case ThemeFetchStatus.Found:
                    return result;
                case ThemeFetchStatus.Transient:
                    madeRequest = true;
                    hadTransientFailure = true;
                    failures.Add(result.Reason ?? "transient provider failure");
                    break;
                case ThemeFetchStatus.NotFound:
                    madeRequest = true;
                    failures.Add(result.Reason ?? "not found");
                    break;
                case ThemeFetchStatus.CandidateUnavailable:
                    madeRequest = true;
                    failures.Add(result.Reason ?? "candidate unavailable");
                    break;
                case ThemeFetchStatus.NotApplicable:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown theme-fetch status {result.Status}.");
            }
        }

        if (hadTransientFailure)
        {
            return ThemeFetchResult.Transient(string.Join("; ", failures));
        }

        return madeRequest
            ? ThemeFetchResult.NotFound(string.Join("; ", failures))
            : ThemeFetchResult.NotApplicable("no enabled provider has a usable series identifier");
    }
}
