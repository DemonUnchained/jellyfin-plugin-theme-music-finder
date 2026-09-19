using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Remembers confirmed catalogue misses so a nightly sweep does not repeatedly request
/// a theme that is not available. A record retains the provider explanation as well as its time,
/// allowing the unresolved-series report to remain useful while the item is in backoff.</summary>
public class AttemptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly string? _legacyPath;
    private readonly IReadOnlySet<string>? _excludedLegacyKeys;
    private readonly string? _fallbackLegacyPath;
    private readonly IReadOnlySet<string>? _fallbackExcludedLegacyKeys;
    private readonly IReadOnlyDictionary<string, string>? _legacyKeyRewrites;
    private Dictionary<string, FailureRecord> _failures = new();

    /// <summary>Set when an existing file could not be read. While set, <see cref="SaveAsync"/>
    /// refuses to replace it with the empty in-memory state.</summary>
    private bool _loadFailed;

    private sealed record FailureRecord(DateTimeOffset AttemptedAtUtc, string? Reason);

    public AttemptStore(
        string path,
        ILogger? logger = null,
        string? legacyPath = null,
        IReadOnlySet<string>? excludedLegacyKeys = null,
        string? fallbackLegacyPath = null,
        IReadOnlySet<string>? fallbackExcludedLegacyKeys = null,
        IReadOnlyDictionary<string, string>? legacyKeyRewrites = null)
    {
        _path = path;
        _logger = logger;
        _legacyPath = legacyPath;
        _excludedLegacyKeys = excludedLegacyKeys;
        _fallbackLegacyPath = fallbackLegacyPath;
        _fallbackExcludedLegacyKeys = fallbackExcludedLegacyKeys;
        _legacyKeyRewrites = legacyKeyRewrites;
    }

    public bool ShouldTry(string catalogueId, int retryAfterDays, DateTimeOffset now)
        => !_failures.TryGetValue(catalogueId, out var failure)
           || now - failure.AttemptedAtUtc >= TimeSpan.FromDays(retryAfterDays);

    public string? GetFailureReason(string catalogueId)
        => _failures.TryGetValue(catalogueId, out var failure)
            ? failure.Reason
            : null;

    public void RecordFailure(
        string catalogueId,
        DateTimeOffset now,
        string? reason = null)
        => _failures[catalogueId] = new FailureRecord(
            now,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

    public async Task LoadAsync(CancellationToken ct)
    {
        _failures = new();
        _loadFailed = false;
        var readPath = _path;
        IReadOnlySet<string>? excludedKeys = null;
        var migratingLegacyHistory = false;
        if (!File.Exists(readPath))
        {
            if (!string.IsNullOrWhiteSpace(_legacyPath) && File.Exists(_legacyPath))
            {
                readPath = _legacyPath;
                excludedKeys = _excludedLegacyKeys;
                migratingLegacyHistory = true;
            }
            else if (!string.IsNullOrWhiteSpace(_fallbackLegacyPath)
                     && File.Exists(_fallbackLegacyPath))
            {
                readPath = _fallbackLegacyPath;
                excludedKeys = _fallbackExcludedLegacyKeys;
                migratingLegacyHistory = true;
            }
            else
            {
                return;
            }
        }

        try
        {
            await using var stream = File.OpenRead(readPath);
            _failures = await ReadFailuresAsync(stream, ct).ConfigureAwait(false);

            if (migratingLegacyHistory)
            {
                var removed = 0;
                if (excludedKeys is not null)
                {
                    foreach (var key in excludedKeys)
                    {
                        if (_failures.Remove(key)) removed++;
                    }
                }

                var rewritten = RewriteLegacyKeys();
                _logger?.LogInformation(
                    "Migrated {Count} attempt-history record(s) from {LegacyPath} to {Path}; invalidated {Removed} corrected-match record(s) and rewrote {Rewritten} revoked-alias record(s).",
                    _failures.Count,
                    readPath,
                    _path,
                    removed,
                    rewritten);
            }
        }
        catch (JsonException ex)
        {
            // The bytes are genuinely unusable, so a later save cannot make things worse — but
            // it must not silently erase them either. Move the file aside so the user keeps it.
            _failures = new();
            var quarantine = readPath + ".invalid";
            try
            {
                File.Move(readPath, quarantine, overwrite: true);
                _logger?.LogWarning(
                    ex,
                    "Attempt history at {Path} is not valid JSON; moved it to {Quarantine} and started a fresh history. Every series becomes eligible again on this run.",
                    readPath,
                    quarantine);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _loadFailed = true;
                _logger?.LogWarning(
                    moveEx,
                    "Attempt history at {Path} is not valid JSON and could not be moved aside; it will be left untouched and no history will be saved this run.",
                    readPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _failures = new();
            _loadFailed = true;
            _logger?.LogWarning(
                ex,
                "Could not read attempt history at {Path}; continuing with an empty history and skipping the save at the end of this run so the existing file is preserved.",
                readPath);
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        if (_loadFailed)
        {
            _logger?.LogWarning(
                "Not saving attempt history to {Path}: it could not be read at the start of this run, and writing now would replace it with an empty history.",
                _path);
            return;
        }

        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmpPath = _path + ".tmp";
        try
        {
            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    _failures,
                    JsonOptions,
                    ct).ConfigureAwait(false);
            }

            File.Move(tmpPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    private static async Task<Dictionary<string, FailureRecord>> ReadFailuresAsync(
        Stream stream,
        CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Attempt history root must be a JSON object.");

        var failures = new Dictionary<string, FailureRecord>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            FailureRecord? record = null;
            if (property.Value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    property.Value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var legacyTimestamp))
            {
                // v1-v5 stored only a timestamp. Keep it, with no invented explanation.
                record = new FailureRecord(legacyTimestamp, null);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                record = property.Value.Deserialize<FailureRecord>(JsonOptions);
            }

            if (record is null || record.AttemptedAtUtc == default)
                throw new JsonException($"Attempt-history entry '{property.Name}' is invalid.");
            failures[property.Name] = record;
        }

        return failures;
    }

    private int RewriteLegacyKeys()
    {
        if (_legacyKeyRewrites is null) return 0;
        var rewritten = 0;
        foreach (var (oldKey, newKey) in _legacyKeyRewrites)
        {
            if (!_failures.Remove(oldKey, out var oldRecord)) continue;
            if (!_failures.TryGetValue(newKey, out var current)
                || oldRecord.AttemptedAtUtc > current.AttemptedAtUtc)
            {
                _failures[newKey] = oldRecord;
            }

            rewritten++;
        }

        return rewritten;
    }
}
