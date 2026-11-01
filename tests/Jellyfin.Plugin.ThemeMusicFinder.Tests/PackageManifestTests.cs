using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class PackageManifestTests
{
    [Fact]
    public void ManifestWhitelistsPluginAndRuntimeDependency()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "src",
            "Jellyfin.Plugin.ThemeMusicFinder",
            "meta.json");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var assemblies = document.RootElement
            .GetProperty("assemblies")
            .EnumerateArray()
            .Select(element => element.GetString() ?? throw new InvalidDataException("Null assembly name"))
            .ToArray();

        Assert.Equal(
            ["Jellyfin.Plugin.ThemeMusicFinder.dll", "YoutubeExplode.dll"],
            assemblies);
    }

    [Fact]
    public void ProjectManifestAndBuildMetadataAgree()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src",
            "Jellyfin.Plugin.ThemeMusicFinder", "Jellyfin.Plugin.ThemeMusicFinder.csproj"));
        string Property(string name) => project.Descendants(name).Single().Value;
        var version = Property("Version");
        var framework = Property("TargetFramework");
        var jellyfinVersions = project.Descendants("PackageReference")
            .Where(element => ((string?)element.Attribute("Include"))?.StartsWith("Jellyfin.") == true)
            .Select(element => (string)element.Attribute("Version")!).Distinct().ToArray();
        var jellyfinVersion = Assert.Single(jellyfinVersions);
        var abi = jellyfinVersion + ".0";

        Assert.Equal(version, Property("AssemblyVersion"));
        Assert.Equal(version, Property("FileVersion"));
        Assert.Equal(version, typeof(ThemeDiscoveryAuditScheduledTask).Assembly.GetName().Version!.ToString());
        Assert.Equal("net10.0", framework);
        Assert.Equal("12.4.0.0", abi); // Keep development separate from the stable 12.1 artifact.

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "src", "Jellyfin.Plugin.ThemeMusicFinder", "meta.json")));
        Assert.Equal(version, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(abi, manifest.RootElement.GetProperty("targetAbi").GetString());
        Assert.Equal("Active", manifest.RootElement.GetProperty("status").GetString());

        var buildMetadata = File.ReadAllText(Path.Combine(root, "build.yaml"));
        Assert.Contains($"version: \"{version}\"", buildMetadata);
        Assert.Contains($"targetAbi: \"{abi}\"", buildMetadata);
        Assert.Contains($"framework: \"{framework}\"", buildMetadata);
        var buildScript = File.ReadAllText(Path.Combine(root, "build.sh"));
        Assert.Contains($"version=\"{version}\"", buildScript);

        var testProject = XDocument.Load(Path.Combine(root, "tests",
            "Jellyfin.Plugin.ThemeMusicFinder.Tests", "Jellyfin.Plugin.ThemeMusicFinder.Tests.csproj"));
        Assert.Equal(framework, testProject.Descendants("TargetFramework").Single().Value);
        foreach (var reference in testProject.Descendants("PackageReference")
            .Where(element => ((string?)element.Attribute("Include"))?.StartsWith("Jellyfin.") == true))
            Assert.Equal(jellyfinVersion, (string?)reference.Attribute("Version"));

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var pinnedPackages = Regex.Matches(workflow, @"ThemeMusicFinder_(\d+\.\d+\.\d+\.\d+)\.zip");
        Assert.NotEmpty(pinnedPackages);
        Assert.All(pinnedPackages.Cast<Match>(), match => Assert.Equal(version, match.Groups[1].Value));
        var pluginFolders = Regex.Matches(workflow, @"Theme Music Finder_(\d+\.\d+\.\d+\.\d+)");
        Assert.NotEmpty(pluginFolders);
        Assert.All(pluginFolders.Cast<Match>(), match => Assert.Equal(version, match.Groups[1].Value));
        Assert.Contains($"jellyfin/jellyfin:{jellyfinVersion[..jellyfinVersion.LastIndexOf('.')]}", workflow);
        Assert.Contains("--prerelease", workflow);
    }

    [Fact]
    public void InstallAndDiscoveryDocsDescribeCurrentPreview()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "src", "Jellyfin.Plugin.ThemeMusicFinder", "meta.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var abi = manifest.RootElement.GetProperty("targetAbi").GetString();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains($"| Development audit preview (prerelease) | {version} | {abi} |", readme);
        Assert.Contains("| Stable/catalog (separate artifact) | 1.2.1.9 | 12.1.0.0 |", readme);
        Assert.Contains($"/config/plugins/Theme Music Finder_{version}/", readme);
        Assert.Contains($"dist/ThemeMusicFinder_{version}.zip", readme);
        Assert.Contains("EnableAutomaticDiscovery=true", readme);
        Assert.Contains("EnableAutomaticDiscoveryWrites=false", readme);
        Assert.DoesNotContain("never performs a generic YouTube search", readme);
        Assert.DoesNotContain("Theme Music Finder_1.2.1.9/", readme);
        Assert.DoesNotContain("dist/ThemeMusicFinder_1.2.1.9.zip", readme);
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains($"## {version} (prerelease / audit preview)", changelog);
        Assert.Contains($"targetAbi {abi}", changelog);
        Assert.Contains("EnableAutomaticDiscoveryWrites=false", changelog);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jellyfin.Plugin.ThemeMusicFinder.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
