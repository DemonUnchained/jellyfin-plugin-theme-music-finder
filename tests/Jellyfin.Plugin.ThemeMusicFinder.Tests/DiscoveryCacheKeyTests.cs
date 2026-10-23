namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class DiscoveryCacheKeyTests
{
    [Fact]
    public void EnabledDiscoveryHasStableVersionedSuffix()
    {
        var config = new PluginConfiguration
        {
            EnableAutomaticDiscovery = true,
            EnableAutomaticDiscoveryWrites = true,
            EnableSoundCloudDiscovery = true,
            EnableBilibiliDiscovery = true
        };
        Assert.Equal("autodiscovery-v2-s1-b1", AttemptKeySuffix.Discovery(config));
        Assert.Equal(AttemptKeySuffix.Discovery(config), AttemptKeySuffix.Discovery(config));
    }

    [Fact]
    public void DisablingOrChangingSourcesInvalidatesOnlyRelevantMisses()
    {
        var config = new PluginConfiguration { EnableAutomaticDiscovery = true, EnableAutomaticDiscoveryWrites = true };
        var original = AttemptKeySuffix.Discovery(config);
        config.EnableAutomaticDiscoveryWrites = false;
        Assert.Null(AttemptKeySuffix.Discovery(config));
        config.EnableAutomaticDiscoveryWrites = true;
        config.EnableSoundCloudDiscovery = false;
        Assert.NotEqual(original, AttemptKeySuffix.Discovery(config));
        config.EnableAutomaticDiscovery = false;
        Assert.Null(AttemptKeySuffix.Discovery(config));
    }

    [Fact]
    public void ExistingCuratedAndAnimeSuffixesRemainPartOfAttemptKey()
    {
        var combined = AttemptKeySuffix.Combine(
            "curated-abc", "anime-def",
            AttemptKeySuffix.Discovery(new PluginConfiguration { EnableAutomaticDiscoveryWrites = true }));
        Assert.Equal("curated-abc.anime-def.autodiscovery-v2-s1-b1", combined);
    }
}
