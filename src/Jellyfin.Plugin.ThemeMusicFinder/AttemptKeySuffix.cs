namespace Jellyfin.Plugin.ThemeMusicFinder;

internal static class AttemptKeySuffix
{
    /// <summary>Combines independent matching inputs into one deterministic cache-key suffix.
    /// A change to either an AnimeThemes alias or a curated source therefore retries the affected
    /// series immediately without invalidating any unrelated miss.</summary>
    // Changing the decision engine or enabled discovery sources invalidates only
    // confirmed-miss backoff keys, so existing unresolved shows are retried on upgrade.
    // Keep this stable between nightly scans; otherwise every scan could hammer sources.
    internal static string? Discovery(PluginConfiguration config)
        => !config.EnableAutomaticDiscovery || !config.EnableAutomaticDiscoveryWrites
            ? null
            : "autodiscovery-v2-s" + (config.EnableSoundCloudDiscovery ? "1" : "0")
              + "-b" + (config.EnableBilibiliDiscovery ? "1" : "0");

    internal static string? Combine(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join('.', present);
    }
}
