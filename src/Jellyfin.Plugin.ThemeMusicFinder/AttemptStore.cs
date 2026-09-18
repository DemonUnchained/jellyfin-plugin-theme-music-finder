using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Remembers which TVDB ids were tried and failed, so a nightly sweep
/// does not re-request a theme that does not exist. Only failures are stored —
/// a success leaves theme.mp3 on disk, which is its own record.</summary>
public class AttemptStore(string path, ILogger? logger = null)
{
    private Dictionary<string, DateTimeOffset> _failures = new();

    /// <summary>Set when <see cref="LoadAsync"/> could not read an existing file. While set,
    /// <see cref="SaveAsync"/> refuses to write: saving here would replace a file we failed to
    /// read with the empty in-memory state, destroying the whole backoff history over what may
    /// be a momentary read error.</summary>
    private bool _loadFailed;

    public bool ShouldTry(string tvdbId, int retryAfterDays, DateTimeOffset now)
        => !_failures.TryGetValue(tvdbId, out var last)
           || now - last >= TimeSpan.FromDays(retryAfterDays);

    public void RecordFailure(string tvdbId, DateTimeOffset now) => _failures[tvdbId] = now;

    public async Task LoadAsync(CancellationToken ct)
    {
        _failures = new();
        _loadFailed = false;
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = File.OpenRead(path);
            _failures = await JsonSerializer
                .DeserializeAsync<Dictionary<string, DateTimeOffset>>(stream, cancellationToken: ct)
                .ConfigureAwait(false) ?? new();
        }
        catch (JsonException ex)
        {
            // The bytes are genuinely unusable, so a later save cannot make things worse — but
            // it must not silently erase them either. Move the file aside so the user still has
            // it (and so a corrupt file cannot wedge the store into never saving again), then
            // carry on with an empty history.
            _failures = new();
            var quarantine = path + ".invalid";
            try
            {
                File.Move(path, quarantine, overwrite: true);
                logger?.LogWarning(
                    ex,
                    "Attempt history at {Path} is not valid JSON; moved it to {Quarantine} and started a fresh history. Every series becomes eligible again on this run.",
                    path,
                    quarantine);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                // Could not preserve it; refuse to overwrite it either.
                _loadFailed = true;
                logger?.LogWarning(
                    moveEx,
                    "Attempt history at {Path} is not valid JSON and could not be moved aside; it will be left untouched and no history will be saved this run.",
                    path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not read it — the file itself may be perfectly good (locked by a backup
            // agent, an NFS/SMB hiccup, a permission problem). Treat the history as empty for
            // this run but do not write over it.
            _failures = new();
            _loadFailed = true;
            logger?.LogWarning(
                ex,
                "Could not read attempt history at {Path}; continuing with an empty history and skipping the save at the end of this run so the existing file is preserved.",
                path);
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        if (_loadFailed)
        {
            logger?.LogWarning(
                "Not saving attempt history to {Path}: it could not be read at the start of this run, and writing now would replace it with an empty history.",
                path);
            return;
        }

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmpPath = path + ".tmp";
        try
        {
            // The stream must be fully disposed — and therefore flushed and closed — before the
            // rename, otherwise File.Move publishes a still-buffered, zero-byte file (and fails
            // outright on Windows, where File.Create holds the handle with FileShare.None).
            // The rename is the commit point; there is nothing to commit until the write is done.
            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(stream, _failures, cancellationToken: ct).ConfigureAwait(false);
            }

            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }
        }
    }
}
