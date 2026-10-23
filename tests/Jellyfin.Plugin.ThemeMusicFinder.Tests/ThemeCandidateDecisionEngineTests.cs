namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class ThemeCandidateDecisionEngineTests
{
    private static ThemeSearchVideo Video(string title, double seconds = 85, int hits = 1)
        => new("abc123_XYZ9", title, "Network Official", TimeSpan.FromSeconds(seconds), hits);

    [Fact]
    public void ExactMainThemeAccepts()
    {
        var candidate = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist (2023) - Main Title Theme"));
        Assert.True(candidate.Accepted);
        Assert.True(candidate.Score >= ThemeCandidateDecisionEngine.MinimumAutomaticScore);
    }

    [Fact]
    public void RelatedTitleDoesNotCountAsExactTitle()
    {
        var candidate = ThemeCandidateDecisionEngine.Evaluate(
            "From", null, 2022,
            Video("From Dusk Till Dawn main title theme (2022)"));
        Assert.False(candidate.Accepted);
    }

    [Theory]
    [InlineData("The Enfield Poltergeist main theme guitar cover")]
    [InlineData("The Enfield Poltergeist main theme reaction")]
    [InlineData("The Enfield Poltergeist main theme trailer")]
    [InlineData("The Enfield Poltergeist main theme AI generated")]
    public void RejectsMisleadingVersions(string title)
    {
        Assert.False(ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023, Video(title)).Accepted);
    }

    [Fact]
    public void RejectsOutsideDurationRange()
    {
        Assert.False(ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist main title theme", 900)).Accepted);
    }

    [Fact]
    public void CommonTitleNeedsYear()
    {
        Assert.False(ThemeCandidateDecisionEngine.Evaluate(
            "The Office", null, 2005, Video("The Office main title theme")).Accepted);
        Assert.True(ThemeCandidateDecisionEngine.Evaluate(
            "The Office", null, 2005, Video("The Office (2005) main title theme")).Accepted);
    }

    [Fact]
    public void OriginalTitleCanMatch()
    {
        Assert.True(ThemeCandidateDecisionEngine.Evaluate(
            "Translated Name", "The Enfield Poltergeist", 2023,
            Video("The Enfield Poltergeist (2023) official theme song")).Accepted);
    }

    [Theory]
    [InlineData("The Enfield Poltergeist (2002) main title theme")]
    [InlineData("The Enfield Poltergeist (2024) official TV theme song")]
    public void RejectsDifferentProductionYearEvenWithOfficialThemeCues(string candidate)
    {
        var result = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023, Video(candidate));
        Assert.False(result.Accepted);
        Assert.Contains("different production year", result.Reason);
    }

    [Fact]
    public void FanMadeDescriptionIsRejected()
    {
        var result = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist (2023) opening credits theme")
                with { Description = "Fan made arrangement based on the series" });
        Assert.False(result.Accepted);
    }

    [Fact]
    public void UnclearWinnerIsRejected()
    {
        var first = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist (2023) TV main theme"));
        var second = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist (2023) opening credits theme"));
        var ranked = new[] { first, second }.OrderByDescending(c => c.Score).ToArray();
        Assert.All(ranked, c => Assert.True(c.Accepted));
        Assert.False(ThemeCandidateDecisionEngine.IsClearWinner(ranked));
    }

    [Fact]
    public void IndependentYearAndSeriesContextCanAuthorizeClearMainTheme()
    {
        var result = ThemeCandidateDecisionEngine.Evaluate(
            "The Enfield Poltergeist", null, 2023,
            Video("The Enfield Poltergeist (2023) official TV main title theme"));
        Assert.True(result.Accepted);
        Assert.True(ThemeCandidateDecisionEngine.IsClearWinner([result]));
    }

    [Fact]
    public void QueriesIncludeAlternativeTitle()
    {
        var queries = ThemeCandidateDecisionEngine.BuildQueries(
            "Localized Series", "Original Series", 2020);
        Assert.Equal(3, queries.Count);
        Assert.Contains("Original Series", queries[2]);
    }

    [Fact]
    public void IdentifierlessKeyStableAndDistinct()
    {
        var first = ThemeCandidateDecisionEngine.GetIdentifierlessAttemptKey("Show One", 2000);
        Assert.Equal(first, ThemeCandidateDecisionEngine.GetIdentifierlessAttemptKey("show one", 2000));
        Assert.NotEqual(first, ThemeCandidateDecisionEngine.GetIdentifierlessAttemptKey("Show One", 2010));
    }
}
