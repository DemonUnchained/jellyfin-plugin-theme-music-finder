using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class ThemeDiscoveryAuditTaskTests
{
    [Fact]
    public async Task CancellationResumeAndCompletedRescanPreserveThemeAndConfigurationBytes()
    {
        var root = Directory.CreateTempSubdirectory("theme-audit-task-").FullName;
        try
        {
            var paths = new AuditPaths(root);
            Directory.CreateDirectory(paths.PluginConfigurationsPath);
            var shows = Enumerable.Range(0, 12).Select(index => new Series
            {
                Name = "Isolated Series " + index,
                Path = Directory.CreateDirectory(Path.Combine(root, "series-" + index)).FullName,
                ProductionYear = 2023
            }).ToArray();
            var existing = new Series
            {
                Name = "Existing Theme",
                Path = Directory.CreateDirectory(Path.Combine(root, "existing")).FullName
            };
            var theme = Path.Combine(existing.Path, "theme.mp3");
            await File.WriteAllBytesAsync(theme, [73, 68, 51, 1, 2, 3]);
            var protectedFiles = new Dictionary<string, byte[]> { [theme] = await File.ReadAllBytesAsync(theme) };
            foreach (var file in new[] { "ThemeMusicFinder.attempts-v6.json",
                "ThemeMusicFinder.source-overrides.json", "ThemeMusicFinder.animethemes-overrides.json",
                "ThemeMusicFinder.xml" })
            {
                var path = Path.Combine(paths.PluginConfigurationsPath, file);
                var bytes = System.Text.Encoding.UTF8.GetBytes("preserve exact bytes: " + file);
                await File.WriteAllBytesAsync(path, bytes);
                protectedFiles.Add(path, bytes);
            }

            // No yt-dlp exists in this temporary program-data directory, so the real task
            // cannot contact a provider or execute an audio tool. Provider winner/ambiguity
            // behavior is covered separately by AutonomousThemePipelineTests.
            var task = new ThemeDiscoveryAuditScheduledTask(new FakeLibraryManager(
                [.. shows, existing, new Series { Name = "Missing path" }]), paths,
                NullLoggerFactory.Instance);
            var reportPath = Path.Combine(paths.PluginConfigurationsPath,
                "ThemeMusicFinder.discovery-audit.json");
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(
                new CallbackProgress(_ =>
                {
                    // The periodic checkpoint must exist before cancellation is requested.
                    if (File.Exists(reportPath)) cancellation.Cancel();
                }), cancellation.Token));
            var interrupted = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!;
            Assert.False(interrupted["complete"]!.GetValue<bool>());
            Assert.InRange(interrupted["entries"]!.AsArray().Count, 10, 11);
            Assert.False(File.Exists(reportPath + ".tmp"));

            // Seed a synthetic earlier decision to distinguish resume from reevaluation.
            var first = interrupted["entries"]![0]!;
            first["status"] = "candidate";
            first["reason"] = "synthetic retained decision";
            interrupted["entries"]![1]!["status"] = "transient";
            interrupted["entries"]![2]!["reason"] = "stale unavailable tool";
            await File.WriteAllTextAsync(reportPath, interrupted.ToJsonString());
            await task.ExecuteAsync(new CallbackProgress(_ => { }), CancellationToken.None);
            var resumed = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!;
            Assert.True(resumed["complete"]!.GetValue<bool>());
            Assert.Equal(12, resumed["entries"]!.AsArray().Count);
            Assert.Equal("synthetic retained decision", resumed["entries"]![0]!["reason"]!.GetValue<string>());
            Assert.DoesNotContain(resumed["entries"]!.AsArray(), entry =>
                entry!["status"]!.GetValue<string>() == "transient");
            Assert.DoesNotContain(resumed["entries"]!.AsArray(), entry =>
                entry!["reason"]!.GetValue<string>() == "stale unavailable tool");
            Assert.Equal(1, resumed["existingThemesSkipped"]!.GetValue<int>());
            Assert.Equal(1, resumed["missingPathsSkipped"]!.GetValue<int>());

            // A completed report must evaluate again even when the signature is unchanged.
            await task.ExecuteAsync(new CallbackProgress(_ => { }), CancellationToken.None);
            var fresh = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!;
            Assert.All(fresh["entries"]!.AsArray(), entry =>
                Assert.Equal("not-applicable", entry!["status"]!.GetValue<string>()));
            foreach (var (path, bytes) in protectedFiles)
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(root, "theme.mp3", SearchOption.AllDirectories));
            Assert.False(File.Exists(reportPath + ".tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    private sealed class AuditPaths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string PluginConfigurationsPath => Path.Combine(root, "configurations");
        public string WebPath => throw new NotSupportedException();
        public string ProgramSystemPath => throw new NotSupportedException();
        public string DataPath => throw new NotSupportedException();
        public string ImageCachePath => throw new NotSupportedException();
        public string PluginsPath => throw new NotSupportedException();
        public string LogDirectoryPath => throw new NotSupportedException();
        public string ConfigurationDirectoryPath => throw new NotSupportedException();
        public string SystemConfigurationFilePath => throw new NotSupportedException();
        public string CachePath => throw new NotSupportedException();
        public string TempDirectory => throw new NotSupportedException();
        public string VirtualDataPath => throw new NotSupportedException();
        public string TrickplayPath => throw new NotSupportedException();
        public string BackupPath => throw new NotSupportedException();
        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();
        public void CreateAndCheckMarker(string path, string markerName, bool recursive)
            => throw new NotSupportedException();
    }
}
