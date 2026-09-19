using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Loads user-maintained stable-ID-to-title aliases. Aliases only influence catalogue
/// discovery; AnimeThemes must still return that exact title or synonym in the correct year.</summary>
internal sealed class AnimeThemeTitleOverrideStore(
    IReadOnlyDictionary<string, string[]> mappings)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal IReadOnlyDictionary<string, string[]> Mappings { get; } = mappings;

    internal static async Task<AnimeThemeTitleOverrideStore> LoadOrCreateAsync(
        string path,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmpPath = path + ".tmp";
                try
                {
                    await using (var stream = File.Create(tmpPath))
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            AnimeThemesProvider.DefaultTitleOverrides,
                            JsonOptions,
                            ct).ConfigureAwait(false);
                    }

                    File.Move(tmpPath, path, overwrite: false);
                }
                finally
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }

                logger.LogInformation(
                    "Created AnimeThemes title-override file at {Path}.",
                    path);
            }

            await using var input = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string[]>>(
                input,
                JsonOptions,
                ct).ConfigureAwait(false) ?? [];
            var validated = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in loaded)
            {
                if (!IsValidKey(key) || values is null) continue;
                var aliases = values
                    .Where(value => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 200)
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray();
                if (aliases.Length > 0) validated[key.Trim()] = aliases;
            }

            logger.LogInformation(
                "Loaded {Count} AnimeThemes stable-ID title override(s) from {Path}.",
                validated.Count,
                path);
            return new AnimeThemeTitleOverrideStore(validated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(
                ex,
                "Could not load AnimeThemes title overrides from {Path}; built-in verified aliases remain active.",
                path);
            return new AnimeThemeTitleOverrideStore(
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool IsValidKey(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1) return false;
        var provider = value[..separator];
        var id = value[(separator + 1)..];
        return (provider.Equals("tvdb", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("tmdb", StringComparison.OrdinalIgnoreCase))
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            && numericId > 0;
    }
}
