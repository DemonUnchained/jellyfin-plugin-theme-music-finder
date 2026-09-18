using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Why a fetch produced no bytes. The distinction is load-bearing: a
/// <see cref="NotFound"/> is a fact about the upstream catalogue and is worth remembering for
/// days, whereas a <see cref="Transient"/> says nothing about whether a theme exists and must
/// never be recorded as a failure — doing so turns one bad hour at a free, no-SLA service into
/// a silent library-wide outage for the length of the backoff window.</summary>
public enum ThemeFetchStatus
{
    /// <summary>Validated MP3 bytes were returned.</summary>
    Found,

    /// <summary>The upstream says this series has no theme. Safe to back off.</summary>
    NotFound,

    /// <summary>The upstream could not answer, or answered with something unusable. Retry
    /// on the next run; do not record a failure.</summary>
    Transient,

    /// <summary>This provider cannot safely identify the series. The provider chain may try
    /// another catalogue, but no request was made and this result must not earn backoff.</summary>
    NotApplicable
}

/// <summary>The outcome of one upstream lookup.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Body">Validated MP3 bytes; non-null if and only if <paramref name="Status"/> is
/// <see cref="ThemeFetchStatus.Found"/>.</param>
/// <param name="Reason">Short human-readable detail for the log. Null for
/// <see cref="ThemeFetchStatus.Found"/>.</param>
public sealed record ThemeFetchResult(
    ThemeFetchStatus Status,
    byte[]? Body,
    string? Reason,
    string? Source = null,
    Uri? SourceUri = null)
{
    public static ThemeFetchResult Found(byte[] body, string? source = null, Uri? sourceUri = null)
        => new(ThemeFetchStatus.Found, body, null, source, sourceUri);

    public static ThemeFetchResult NotFound(string reason) => new(ThemeFetchStatus.NotFound, null, reason);

    public static ThemeFetchResult Transient(string reason) => new(ThemeFetchStatus.Transient, null, reason);

    public static ThemeFetchResult NotApplicable(string reason) => new(ThemeFetchStatus.NotApplicable, null, reason);
}

public interface IThemeProvider
{
    /// <summary>Looks up a theme. Implementations must not throw for an ordinary "not found",
    /// and must classify anything that is not a definitive "no such theme" as
    /// <see cref="ThemeFetchStatus.Transient"/>.</summary>
    Task<ThemeFetchResult> FetchAsync(Series series, CancellationToken ct);
}
