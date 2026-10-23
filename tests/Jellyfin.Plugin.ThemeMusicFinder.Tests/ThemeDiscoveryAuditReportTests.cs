using System.Text.Json;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class ThemeDiscoveryAuditReportTests
{
    [Fact]
    public async Task ReportPersistsCandidateEvidenceWithoutCreatingMusic()
    {
        var root = Path.Combine(Path.GetTempPath(), "ThemeMusicFinderAudit-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ThemeMusicFinder.discovery-audit.json");
            var report = new ThemeDiscoveryAuditReport
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                TotalSeriesScanned = 3,
                ExistingThemesSkipped = 1,
                MissingPathsSkipped = 0,
                CandidateCount = 1,
                RejectedCount = 1,
                TransientCount = 0,
                NotApplicableCount = 0,
                Entries =
                [
                    new ThemeDiscoveryAuditEntry(
                        "The Enfield Poltergeist", null, 2023, "440134", "235851",
                        "/media/TV/The Enfield Poltergeist", "candidate",
                        "exact series and year; metadata-only",
                        "YouTube", "https://www.youtube.com/watch?v=abc123_XYZ9",
                        "The Enfield Poltergeist (2023) TV main title theme", 99),
                    new ThemeDiscoveryAuditEntry(
                        "Ambiguous Series", null, 2015, "1002", null,
                        "/media/TV/Ambiguous Series", "rejected",
                        "competing candidates", null, null, null, null)
                ]
            };
            await ThemeDiscoveryAuditReport.WriteAtomicAsync(path, report,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(Path.Combine(root, "theme.mp3")));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(3, json.RootElement.GetProperty("totalSeriesScanned").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("candidateCount").GetInt32());
            Assert.Equal("candidate",
                json.RootElement.GetProperty("entries")[0].GetProperty("status").GetString());
            Assert.Equal("YouTube",
                json.RootElement.GetProperty("entries")[0].GetProperty("source").GetString());
            Assert.Equal(99,
                json.RootElement.GetProperty("entries")[0].GetProperty("confidenceScore").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IncompleteAuditResumesOnlyWithMatchingLibrarySignature()
    {
        var root = Path.Combine(Path.GetTempPath(), "ThemeMusicFinderResume-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "ThemeMusicFinder.discovery-audit.json");
            var report = new ThemeDiscoveryAuditReport
            {
                Complete = false,
                ScanSignature = "library-v2-abc",
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                TotalSeriesScanned = 25,
                CandidateCount = 1,
                Entries =
                [
                    new ThemeDiscoveryAuditEntry(
                        "Existing decision", null, 2023, null, null,
                        "/media/TV/Existing decision", "candidate",
                        "metadata-only candidate", "YouTube",
                        "https://www.youtube.com/watch?v=abc123_XYZ9",
                        "Existing decision theme", 99)
                ]
            };

            await ThemeDiscoveryAuditReport.WriteAtomicAsync(path, report,
                CancellationToken.None);
            var resumed = await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, "library-v2-abc", CancellationToken.None);
            Assert.NotNull(resumed);
            Assert.Single(resumed.Entries);
            Assert.False(resumed.Complete);
            Assert.Null(await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, "changed-source-config", CancellationToken.None));

            var completed = new ThemeDiscoveryAuditReport
            {
                Complete = true,
                ScanSignature = "library-v2-abc",
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                TotalSeriesScanned = 25,
                CandidateCount = 1,
                Entries = resumed.Entries
            };
            await ThemeDiscoveryAuditReport.WriteAtomicAsync(path, completed,
                CancellationToken.None);
            Assert.Null(await ThemeDiscoveryAuditReport.LoadIncompleteAsync(
                path, "library-v2-abc", CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AuditTaskIsAvailableOnDemandButNeverSchedulesItself()
    {
        var task = new ThemeDiscoveryAuditScheduledTask(null!, null!, null!);
        Assert.Contains("read only", task.Name);
        Assert.Empty(task.GetDefaultTriggers());
        Assert.Equal("ThemeMusicFinderAudit", task.Key);
    }

    [Fact]
    public void NewSourceWritesAreOptIn()
    {
        var config = new PluginConfiguration();
        Assert.True(config.EnableAutomaticDiscovery);
        Assert.False(config.EnableAutomaticDiscoveryWrites);
        Assert.Null(AttemptKeySuffix.Discovery(config));
    }
}
