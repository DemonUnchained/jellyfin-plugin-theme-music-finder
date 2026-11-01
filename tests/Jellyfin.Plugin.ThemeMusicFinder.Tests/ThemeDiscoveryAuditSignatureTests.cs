using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class ThemeDiscoveryAuditSignatureTests
{
    private static Series Show() => new()
    {
        Path = "/isolated-library/Example Series",
        Name = "Example Series",
        OriginalTitle = "Original Series Title",
        ProductionYear = 2023,
        ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "100", ["Tmdb"] = "200" }
    };

    private static string Signature(IEnumerable<Series> items, PluginConfiguration? config = null,
        Func<Series, bool>? isAnime = null,
        string revision = AutonomousThemeDiscoveryProvider.AuditRulesRevision)
        => ThemeDiscoveryAuditScheduledTask.CreateScanSignature(
            items, config ?? new(), isAnime ?? (_ => false), revision);

    [Theory]
    [InlineData("path")]
    [InlineData("title")]
    [InlineData("original-title")]
    [InlineData("year")]
    [InlineData("tvdb")]
    [InlineData("tmdb")]
    [InlineData("remove-id")]
    public async Task ChangedIdentityCannotResumeEarlierDecisions(string field)
    {
        var show = Show();
        var original = Signature([show]);
        var root = Directory.CreateTempSubdirectory("theme-audit-identity-").FullName;
        try
        {
            var path = Path.Combine(root, "audit.json");
            await ThemeDiscoveryAuditReport.WriteAtomicAsync(path, new()
            {
                Complete = false,
                ScanSignature = original,
                Entries = [new(show.Name, show.OriginalTitle, show.ProductionYear, "100", "200",
                    show.Path, "rejected", "earlier metadata decision", null, null, null, null)]
            }, CancellationToken.None);
            Assert.NotNull(await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, original, CancellationToken.None));

            switch (field)
            {
                case "path": show.Path += " relocated"; break;
                case "title": show.Name += " corrected"; break;
                case "original-title": show.OriginalTitle += " corrected"; break;
                case "year": show.ProductionYear = 2024; break;
                case "tvdb": show.SetProviderId(MetadataProvider.Tvdb, "101"); break;
                case "tmdb": show.SetProviderId(MetadataProvider.Tmdb, "201"); break;
                case "remove-id": show.ProviderIds.Clear(); break;
            }

            Assert.Null(await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, Signature([show]), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SourceSwitchesAndMatchingRuleRevisionInvalidateSignature()
    {
        var show = Show();
        var baseline = Signature([show]);
        Assert.NotEqual(baseline, Signature([show], new() { EnableSoundCloudDiscovery = false }));
        Assert.NotEqual(baseline, Signature([show], new() { EnableBilibiliDiscovery = false }));
        Assert.NotEqual(baseline, Signature([show], revision: "future-discovery-rules"));
        Assert.Equal(baseline, Signature([show], new()
        {
            EnableAutomaticDiscovery = false,
            EnableAutomaticDiscoveryWrites = true,
            RetryAfterDays = 30,
            EnableLoudnessNormalization = false
        })); // These switches do not affect the independent, read-only audit.
    }

    [Theory]
    [InlineData("genre", true)]
    [InlineData("genre", false)]
    [InlineData("library-name", true)]
    [InlineData("library-path", true)]
    public void ChangedAnimeApplicabilityInvalidatesSignature(string change, bool bilibili)
    {
        var show = Show();
        var folder = new Folder { Name = "TV", Path = "/isolated-library/tv" };
        var library = new FakeLibraryManager(show) { CollectionFolders = _ => [folder] };
        var provider = new AutonomousThemeDiscoveryProvider(null!, null!, library,
            executablePathOverride: "unused");
        var config = new PluginConfiguration { EnableBilibiliDiscovery = bilibili };
        var baseline = Signature([show], config, isAnime: provider.IsLikelyAnime);
        switch (change)
        {
            case "genre": show.Genres = ["Animation"]; break;
            case "library-name": folder.Name = "Anime TV"; break;
            case "library-path": folder.Path = "/isolated-library/anime"; break;
        }

        Assert.NotEqual(baseline, Signature([show], config, isAnime: provider.IsLikelyAnime));
    }

    [Fact]
    public void SignatureIsIndependentOfEnumerationOrderAndCurrentCulture()
    {
        var first = Show();
        var second = Show();
        second.Name = "Second Series"; // Even duplicate paths must sort deterministically.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var baseline = Signature([first, second]);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(baseline, Signature([second, first]));
            Assert.NotEqual(baseline, Signature([first]));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void FieldSeparatorsInMetadataCannotCollide()
    {
        var first = Show();
        var second = Show();
        first.Path = "/isolated-library/TV|Special";
        first.Name = "Show";
        second.Path = "/isolated-library/TV";
        second.Name = "Special|Show";
        Assert.NotEqual(Signature([first]), Signature([second]));
    }
}
