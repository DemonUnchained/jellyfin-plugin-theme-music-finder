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
            Video("The Enfield Poltergeist official theme song")).Accepted);
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
