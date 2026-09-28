using System.Text.Json;

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
            .Select(element => element.GetString())
            .ToArray();

        Assert.Equal(
            ["Jellyfin.Plugin.ThemeMusicFinder.dll", "YoutubeExplode.dll"],
            assemblies);
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
