namespace Jellyfin.Plugin.ThemeMusicFinder;

internal static class AttemptKeySuffix
{
    /// <summary>Combines independent matching inputs into one deterministic cache-key suffix.
    /// A change to either an AnimeThemes alias or a curated source therefore retries the affected
    /// series immediately without invalidating any unrelated miss.</summary>
    internal static string? Combine(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join('.', present);
    }
}
