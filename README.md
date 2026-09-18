# Theme Music Finder for Jellyfin 12

Theme Music Finder scans Jellyfin TV series, looks up missing themes in the public
Plex TV Themes catalog, and writes a validated `theme.mp3` into the series folder.
When Plex has no match, it can fall back to ThemerrDB's curated TMDB-to-YouTube
mapping and convert that audio to MP3 with Jellyfin's FFmpeg.

This build targets the Jellyfin 12.1 plugin ABI and .NET 10. It works without
`yt-dlp`, cookies, an API key, or an extra container.

## What it does

- Runs every day at 3:00 AM as the **Find missing theme music** scheduled task.
- Can also check a series immediately when Jellyfin adds it.
- Scans physical TV series only; it does not touch movies, music, episodes, or
  virtual placeholder items.
- Uses the TVDB identifier already stored in Jellyfin metadata to query
  `tvthemes.plexapp.com`. This is much safer than guessing from a show title.
- After a confirmed Plex miss, optionally queries ThemerrDB by the TMDB identifier
  already in Jellyfin metadata. Plex outages and rate limits do not trigger the
  fallback.
- Downloads ThemerrDB's curated YouTube source with YoutubeExplode and converts it
  to a real MP3 using Jellyfin's configured FFmpeg binary.
- Saves the result as `<series folder>/theme.mp3`.
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

The catalogues are free and have no service guarantee. Coverage is good for many
established TV series but incomplete for new, obscure, and some anime titles.
Series need a TVDB ID for Plex or a TMDB ID for ThemerrDB; title guessing is never
used.

## Requirements

- Jellyfin Server 12.1.
- The official Jellyfin 12 runtime (.NET 10).
- Internet access from the Jellyfin container/server.
- Write access from Jellyfin to the TV library folders.

## Install on your Unraid Jellyfin container

1. Stop the Jellyfin container.
2. Create this folder:

   ```text
   /mnt/user/appdata/jellyfin/config/plugins/Theme Music Finder_1.1.0.0/
   ```

3. Extract both `Jellyfin.Plugin.ThemeMusicFinder.dll` and `YoutubeExplode.dll`
   from the release ZIP into that folder.
4. Start Jellyfin.
5. Open **Dashboard → Plugins → My Plugins → Theme Music Finder** and set the
   retry interval. Immediate checking and the ThemerrDB fallback are enabled by
   default.
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

The installable ZIP is written to `dist/ThemeMusicFinder_1.1.0.0.zip`.

## Source and license

This Jellyfin 12 build is derived from the MIT-licensed
[Bitstorm Labs Theme Songs plugin](https://github.com/bitstorm-labs/jellyfin-theme-songs).
The original safety-oriented download and test architecture is retained and
ported to Jellyfin 12. The binary package also includes the MIT-licensed
YoutubeExplode dependency. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
