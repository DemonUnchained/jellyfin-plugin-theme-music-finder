using Jellyfin.Plugin.ThemeMusicFinder;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void DefaultsToSevenDays()
        => Assert.Equal(7, new PluginConfiguration().RetryAfterDays);

    /// <summary>I4. The config page's parseInt('') is NaN, which reaches the server as 0 and
    /// disables backoff entirely — the plugin would re-request every missing theme from a free
    /// no-SLA service every night, forever, with nothing in the UI or the log to say so. The
    /// same value is settable straight through the plugin configuration API, so the clamp has
    /// to be here and not only in the browser.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(365, 365)]
    public void RetryAfterDaysIsClampedToAtLeastOne(int assigned, int expected)
    {
        var config = new PluginConfiguration { RetryAfterDays = assigned };
        Assert.Equal(expected, config.RetryAfterDays);
    }

    [Fact]
    public void ItemAddedHookIsOnByDefault()
        => Assert.True(new PluginConfiguration().EnableItemAddedHook);

    [Fact]
    public void ThemerrFallbackIsOnByDefault()
        => Assert.True(new PluginConfiguration().EnableThemerrFallback);

    [Fact]
    public void AnimeThemesAndNormalizationAreOnByDefault()
    {
        var config = new PluginConfiguration();
        Assert.True(config.EnableAnimeThemes);
        Assert.True(config.EnableLoudnessNormalization);
        Assert.Equal(-18.0, config.NormalizationTargetLufs);
    }

    [Theory]
    [InlineData(-30, -24)]
    [InlineData(-18, -18)]
    [InlineData(-8, -12)]
    public void LoudnessTargetIsClamped(double assigned, double expected)
    {
        var config = new PluginConfiguration { NormalizationTargetLufs = assigned };
        Assert.Equal(expected, config.NormalizationTargetLufs);
    }

    [Fact]
    public void EveryConfigPageSelectorHasAnElement()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.ThemeMusicFinder.configPage.html")!;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "\\bid=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var selectors = System.Text.RegularExpressions.Regex.Matches(
            html, "querySelector\\('\\#([^']+)'\\)");
        Assert.NotEmpty(selectors);
        Assert.All(selectors.Cast<System.Text.RegularExpressions.Match>(), match =>
            Assert.True(ids.Contains(match.Groups[1].Value), "Missing control: " + match.Groups[1].Value));
    }
}
