namespace Jellyfin.Plugin.ThemeMusicFinder;

/// <summary>Coordinates access to the shared <c>ThemeMusicFinder.attempts.json</c> file across every
/// caller that loads/modifies/saves it: the nightly scheduled sweep (<see cref="ThemeMusicFinderScheduledTask"/>)
/// and each item-added handler (<see cref="ItemAddedListener"/>). Without this, a library scan that
/// bulk-adds series while the nightly sweep is running would race load -> modify -> save on the
/// same file and lose recorded failures. Item-added work is background and non-urgent, so queuing
/// behind a long-running sweep (rather than racing it) is the correct trade-off.</summary>
internal static class ThemeMusicFinderGate
{
    public static readonly SemaphoreSlim AttemptsFile = new(1, 1);
}
