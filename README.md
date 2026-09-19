# Theme Music Finder for Jellyfin 12

Theme Music Finder scans Jellyfin TV series, looks up missing themes in ThemerrDB,
strictly matched AnimeThemes openings, and finally Plex TV Themes. It converts and
normalizes new downloads with Jellyfin's FFmpeg, then writes a validated
`theme.mp3` into the series folder.

This build targets the Jellyfin 12.1 plugin ABI and .NET 10. It works without
`yt-dlp`, cookies, an API key, or an extra container.

## What it does

- Runs every day at 3:00 AM as the **Find missing theme music** scheduled task.
- Can also check a series immediately when Jellyfin adds it.
- Scans physical TV series only; it does not touch movies, music, episodes, or
  virtual placeholder items.
- Queries ThemerrDB first by the TMDB identifier already stored in Jellyfin.
- After a confirmed miss or an unusable curated YouTube video, optionally queries
  AnimeThemes. It accepts only an
  exact title/synonym and year match with a safe OP1 audio file; fuzzy matches
  and generic web/YouTube search results are rejected.
- Uses Plex by TVDB ID as the final fallback and rejects the known incorrect
  `Barber of Seville` mapping for *A Knight of the Seven Kingdoms*.
- Downloads ThemerrDB's curated YouTube source with YoutubeExplode, uses a
  60-second per-candidate limit, and converts it to a real MP3 using Jellyfin's
  configured FFmpeg binary. A dead, blocked, streamless, or timed-out video falls
  through to AnimeThemes and Plex.
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
- Logs a final sweep summary with downloaded, not-found, transient, unwritable,
  unexpected-error, skipped, and total-series counts.

The catalogues are free and have no service guarantee. Coverage is good for many
established TV series but incomplete for new, obscure, and some anime titles.
Series need a TVDB ID for Plex or a TMDB ID for ThemerrDB. AnimeThemes uses an
exact title/year match only; generic title guessing is never used.

## Requirements

- Jellyfin Server 12.1.
- The official Jellyfin 12 runtime (.NET 10).
- Internet access from the Jellyfin container/server.
- Write access from Jellyfin to the TV library folders.

## Install on your Unraid Jellyfin container

1. Stop the Jellyfin container.
2. Create this folder:

   ```text
   /mnt/user/appdata/jellyfin/config/plugins/Theme Music Finder_1.2.1.2/
   ```

3. Extract `Jellyfin.Plugin.ThemeMusicFinder.dll`, `YoutubeExplode.dll`, and
   `meta.json` from the release ZIP into that folder. Jellyfin 12.1 requires the
   manifest's assembly declarations to load the plugin and its dependency after
   a restart.
4. Start Jellyfin.
5. Open **Dashboard → Plugins → My Plugins → Theme Music Finder** and set the
   retry interval and loudness target. ThemerrDB, strict AnimeThemes fallback,
   immediate checking, and normalization are enabled by default.
6. For the first sweep, open **Dashboard → Scheduled Tasks → Find missing theme
   music** and select **Run**.

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

## Build and test

Install the .NET 10 SDK, then run:

```bash
dotnet restore Jellyfin.Plugin.ThemeMusicFinder.slnx
dotnet build Jellyfin.Plugin.ThemeMusicFinder.slnx -c Release --no-restore
dotnet test tests/Jellyfin.Plugin.ThemeMusicFinder.Tests/Jellyfin.Plugin.ThemeMusicFinder.Tests.csproj -c Release
./build.sh
```

The installable ZIP is written to `dist/ThemeMusicFinder_1.2.1.2.zip`.

## Source and license

This Jellyfin 12 build is derived from the MIT-licensed
[Bitstorm Labs Theme Songs plugin](https://github.com/bitstorm-labs/jellyfin-theme-songs).
The original safety-oriented download and test architecture is retained and
ported to Jellyfin 12. The binary package also includes the MIT-licensed
YoutubeExplode dependency. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
