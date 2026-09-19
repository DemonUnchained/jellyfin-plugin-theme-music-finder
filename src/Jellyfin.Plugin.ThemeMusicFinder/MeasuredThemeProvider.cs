using System.Diagnostics;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Small per-run decorator used to make provider ordering and latency observable
/// without adding noise to each individual series lookup.</summary>
internal sealed class MeasuredThemeProvider(string name, IThemeProvider inner) : IThemeProvider
{
    private readonly Dictionary<ThemeFetchStatus, int> _outcomes = [];
    private long _elapsedTicks;

    internal int CallCount { get; private set; }

    internal TimeSpan Elapsed => TimeSpan.FromTicks(_elapsedTicks);

    public async Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.FetchAsync(series, ct).ConfigureAwait(false);
            CallCount++;
            _outcomes[result.Status] = _outcomes.GetValueOrDefault(result.Status) + 1;
            return result;
        }
        finally
        {
            _elapsedTicks += Stopwatch.GetElapsedTime(started).Ticks;
        }
    }

    internal void Log(ILogger logger)
    {
        logger.LogInformation(
            "Theme provider {Provider}: {Calls} call(s), {Found} found, {NotFound} not found, {Transient} transient, {CandidateUnavailable} candidate unavailable, {NotApplicable} not applicable; {ElapsedSeconds:F1}s total.",
            name,
            CallCount,
            Get(ThemeFetchStatus.Found),
            Get(ThemeFetchStatus.NotFound),
            Get(ThemeFetchStatus.Transient),
            Get(ThemeFetchStatus.CandidateUnavailable),
            Get(ThemeFetchStatus.NotApplicable),
            Elapsed.TotalSeconds);
    }

    private int Get(ThemeFetchStatus status) => _outcomes.GetValueOrDefault(status);
}
