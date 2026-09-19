using System.Text.Json;

namespace Jellyfin.Plugin.ThemeMusicFinder;

internal sealed class MissingThemeReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public int TotalSeriesScanned { get; init; }

    public required IReadOnlyList<MissingThemeReportEntry> Entries { get; init; }

    public static async Task WriteAtomicAsync(
        string path,
        MissingThemeReport report,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The report path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

internal sealed record MissingThemeReportEntry(
    string Title,
    string? OriginalTitle,
    int? Year,
    string? TvdbId,
    string? TmdbId,
    string? Path,
    string Status,
    string Reason);
