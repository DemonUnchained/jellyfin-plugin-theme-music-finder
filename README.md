# Theme Music Finder for Jellyfin 12

Theme Music Finder scans Jellyfin TV series, first checks exact local stable-ID
overrides for a hand-picked YouTube video, then looks up missing themes in
ThemerrDB, library-aware strictly matched AnimeThemes openings (or a safe first
ending when no opening is available), and finally Plex TV Themes. It converts
and normalizes new downloads with Jellyfin's FFmpeg, then writes a validated
`theme.mp3` into the series folder.

This build targets the Jellyfin 12.1 plugin ABI and .NET 10. It works without
`yt-dlp`, cookies, an API key, or an extra container.

## What it does

- Runs every day at 3:00 AM as the **Find missing theme music** scheduled task.
- Can also check a series immediately when Jellyfin adds it.
- Scans physical TV series only; it does not touch movies, music, episodes, or
  virtual placeholder items.
- Checks `ThemeMusicFinder.source-overrides.json` first for an exact `tvdb:ID` or
  `tmdb:ID` mapped to one hand-picked HTTPS YouTube video. TVDB takes precedence
  when both IDs are present; the plugin never performs a generic YouTube search.
- Queries ThemerrDB next by the TMDB identifier already stored in Jellyfin.
- After a confirmed miss or an unusable curated YouTube video, optionally queries
  AnimeThemes for series in an anime-named library or tagged Animation/Anime. If
  Jellyfin cannot yet identify the library, it still tries the provider rather than
  creating a false negative.
- Accepts only an exact Jellyfin title, original title, catalogue synonym, or
  stable-TVDB/TMDB-bound verified alias plus year match. It prefers a safe OP1 audio
  file and uses a safe ED1 only when no OP1 exists; fuzzy matches and generic
  web/YouTube search results are rejected.
- Uses a lightweight 20-result AnimeThemes title/year search, including a
  punctuation-normalized query when needed, then requests full audio metadata only
  for a locally verified exact match.
- Uses Plex by TVDB ID as the final fallback and rejects the known incorrect
  `Barber of Seville` mapping for *A Knight of the Seven Kingdoms*.
- Downloads a local override or ThemerrDB's curated YouTube source with the
  Trailer Reel `yt-dlp` installation when it is available, with YoutubeExplode
  retained as a self-contained fallback. `yt-dlp` gets a bounded three-minute
  budget for its socket retries; YoutubeExplode keeps a 60-second limit. Both
  convert the result to a real MP3 with Jellyfin's configured FFmpeg binary. A
  dead, blocked, streamless, or timed-out video falls through to later providers.
- Continues to later independent providers after a temporary provider failure;
  if every fallback misses, the temporary result remains retryable and is not
  added to the negative cache.
- Saves the result as `<series folder>/theme.mp3`.
- Normalizes new downloads to -18 LUFS integrated loudness and -1.5 dBTP true
  peak by default. The target and normalization toggle are configurable.
- Logs the selected provider, catalogue IDs, and source URL for every saved theme.
- Refreshes the series after a successful write so Jellyfin notices the theme.

## Safety behavior

- Never overwrites an existing `theme.mp3`.
- Accepts only a plausible MP3 response with an audio MIME type and MP3/ID3
  signature; HTML error pages are rejected.
- Limits final MP3s to 8 MiB, fallback source audio to 50 MiB, and catalogue
  requests to 30 seconds.
- Writes to `theme.mp3.tmp` first and atomically renames it only after the full
  download succeeds.
- Limits catalog traffic to about one request per second.
- Backs off for a configurable number of days when the catalog definitively has
  no theme, while temporary network/server failures remain eligible for the next
  run.
- Contains per-series error isolation, so one bad folder cannot stop the rest of
  a library scan.
- Opens the AnimeThemes circuit after a provider-wide error and logs identical
  transient failures only once per sweep instead of hundreds of times.
- Logs a final sweep summary with downloaded, not-found, transient, unwritable,
  unexpected-error, and total-series counts. Skips are split into existing theme,
  retry backoff, missing ID, missing path, and provider-not-applicable counts.
- Logs per-provider calls, outcomes, elapsed time, AnimeThemes HTTP activity, and
  how many known non-anime series were skipped before any AnimeThemes request.
- Writes `ThemeMusicFinder.missing-themes.json` after every completed full sweep.
  The report records every unresolved title, original title, year, TVDB/TMDB IDs,
  folder, status, and reason, including series currently inside retry backoff.
- Creates `ThemeMusicFinder.animethemes-overrides.json` in the plugin configuration
  folder. It maps stable `tvdb:ID` or `tmdb:ID` keys to intentional AnimeThemes
  title aliases; an alias still requires an exact returned title/synonym and year.
- Creates `ThemeMusicFinder.source-overrides.json` in the same folder. It maps a
  stable `tvdb:ID` or `tmdb:ID` directly to one HTTPS YouTube video URL. URLs are
  normalized and validated, and editing one entry retries only that series.

The catalogues are free and have no service guarantee. Coverage is good for many
established TV series but incomplete for new, obscure, and some anime titles.
Series need a TVDB ID for Plex or a TMDB ID for ThemerrDB. AnimeThemes uses an
exact title/original-title/synonym and year match only; generic title guessing is
never used.

## Requirements

- Jellyfin Server 12.1.
- The official Jellyfin 12 runtime (.NET 10).
- Internet access from the Jellyfin container/server.
- Write access from Jellyfin to the TV library folders.

## Install on your Unraid Jellyfin container

1. Stop the Jellyfin container.
2. Create this folder:

   ```text
   /mnt/user/appdata/jellyfin/config/plugins/Theme Music Finder_1.2.1.7/
   ```

3. Extract `Jellyfin.Plugin.ThemeMusicFinder.dll`, `YoutubeExplode.dll`, and
   `meta.json` from the release ZIP into that folder. Jellyfin 12.1 requires the
   manifest's assembly declarations to load the plugin and its dependency after
   a restart.
4. Start Jellyfin.
5. Open **Dashboard → Plugins → My Plugins → Theme Music Finder** and set the
   retry interval and loudness target. Local source overrides are always checked
   first; ThemerrDB, strict AnimeThemes fallback, immediate checking, and
   normalization are enabled by default.
6. For the first sweep, open **Dashboard → Scheduled Tasks → Find missing theme
   music** and select **Run**.

If another disk-heavy task such as trickplay generation also runs at 3:00 AM,
move one task to a different time in Jellyfin's Scheduled Tasks page. This avoids
avoidable storage contention; it does not change matching results.

Do not replace a plugin DLL while Jellyfin is running. The assembly is loaded by
the server process and may be corrupted if overwritten in place.

## Verify

On Rocinante, count or list downloaded themes with:

```bash
find /mnt/user/data/media/tv /mnt/user/data/media/anime-tv \
  -type f -iname 'theme.mp3' -print
```

To hear them, enable **Play theme songs** in the playback settings of the
Jellyfin client you use. Theme playback is a client feature, so client support
can vary.

## Resolve remaining misses

Open `ThemeMusicFinder.missing-themes.json` in Jellyfin's plugin configuration
folder after a full sweep. The Jellyfin log prints its exact path. For each entry:

1. Correct a wrong or missing title, year, TVDB ID, or TMDB ID in Jellyfin, then
   run the task again.
2. For a verified theme with no catalogue mapping, add one exact stable ID and
   hand-picked YouTube video to `ThemeMusicFinder.source-overrides.json`. For
   example:

   ```json
   {
     "tvdb:407633": "https://www.youtube.com/watch?v=abcdefghijk"
   }
   ```

   Only a single HTTPS YouTube video is accepted; playlists and generic search
   results are rejected. Adding or editing the URL makes only that series
   eligible immediately without deleting the global attempt history.
3. For a confirmed AnimeThemes title variant, add its exact catalogue title to
   `ThemeMusicFinder.animethemes-overrides.json` under the stable ID reported by
   Jellyfin. For example:

   ```json
   {
     "tvdb:74309": [
       "Macross II: Lovers Again"
     ]
   }
   ```

   Editing an override changes only that series' negative-cache key, so it is
   eligible immediately without deleting the global attempt history.
4. Add a hand-picked `theme.mp3` directly to the reported series folder. The
   plugin treats that file as authoritative and never overwrites it.
5. Contribute an approved YouTube mapping to
   [ThemerrDB](https://github.com/LizardByte/ThemerrDB#contributing), or contribute
   missing anime/theme metadata to [AnimeThemes](https://github.com/AnimeThemes).

Confirmed misses use the configured retry window. Version 1.2.1.7 migrates v5
attempt history into a reason-preserving v6 format; direct upgrades from v4 are
also supported. It retains the three valid v1.2.1.6 alias corrections, revokes
the unsafe *When They Cry* live-action-to-anime alias, and rewrites only that
alias-specific cache entry to its normal stable-ID key. Every unrelated backoff
remains intact.

## Build and test

Install the .NET 10 SDK, then run:

```bash
dotnet restore Jellyfin.Plugin.ThemeMusicFinder.slnx
dotnet build Jellyfin.Plugin.ThemeMusicFinder.slnx -c Release --no-restore
dotnet test tests/Jellyfin.Plugin.ThemeMusicFinder.Tests/Jellyfin.Plugin.ThemeMusicFinder.Tests.csproj -c Release
./build.sh
```

The installable ZIP is written to `dist/ThemeMusicFinder_1.2.1.7.zip`.

## Source and license

This Jellyfin 12 build is derived from the MIT-licensed
[Bitstorm Labs Theme Songs plugin](https://github.com/bitstorm-labs/jellyfin-theme-songs).
The original safety-oriented download and test architecture is retained and
ported to Jellyfin 12. The binary package also includes the MIT-licensed
YoutubeExplode dependency. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
