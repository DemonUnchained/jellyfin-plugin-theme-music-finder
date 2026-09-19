# Changelog

## 1.2.1.6

- Adds an editable `ThemeMusicFinder.animethemes-overrides.json` stable-ID alias
  file, preloaded with four verified false-miss fixes: *Macross II*,
  *Norn9: Norn + Nonette*, *When They Cry*, and *ZatsuTabi -That's Journey-*.
- Adds a punctuation- and camel-case-normalized search variant while retaining
  exact local title/synonym and production-year acceptance; fuzzy results remain
  rejected.
- Splits AnimeThemes discovery into a lightweight 20-result search followed by
  full theme metadata only for an exact match, so misses transfer far less data.
- Skips AnimeThemes HTTP for known live-action libraries unless the series is in
  an anime-named library or has Animation/Anime genre metadata. Unknown library
  placement still falls through safely.
- Logs per-provider calls, outcomes, elapsed time, AnimeThemes HTTP counts, and
  the number of non-anime series skipped before HTTP.
- Migrates v1.2.1.5 attempt history instead of discarding it, invalidating only
  the four corrected false-miss records so every other backoff remains intact.
  Adding or changing an override automatically changes only that series' cache
  key, making it eligible immediately without clearing the global cache.

## 1.2.1.5

- Reads AnimeThemes' live `animesynonyms` field, checks Jellyfin's original title,
  and strips a trailing `(YYYY)` only when it matches the series production year.
- Filters AnimeThemes searches by production year while retaining local exact
  title and year validation, preventing buried exact matches and wrong-year picks.
- Prefers a non-NSFW, non-spoiler OP1 and falls back to a similarly safe ED1 only
  when no usable OP1 exists.
- Starts attempt-history generation v4 so false misses cached by 1.2.1.4 are
  immediately eligible for the corrected matcher.
- Splits skipped-series diagnostics by cause and writes a complete
  `ThemeMusicFinder.missing-themes.json` report with titles, years, catalogue IDs,
  paths, statuses, and reasons after every completed full sweep.

## 1.2.1.4

- Gives `yt-dlp` up to three minutes to finish a curated audio candidate while
  bounding its socket, media, fragment, and extractor retries. If it still
  fails, YoutubeExplode now gets its independent fallback attempt and the log
  retains `yt-dlp`'s final diagnostic line.
- Keeps an unusable candidate retryable when later providers miss instead of
  writing a false seven-day negative-cache entry.
- Starts attempt-history generation v3 so false misses written by 1.2.1.3,
  including *A Knight of the Seven Kingdoms*, are eligible immediately.
- Handles nullable AnimeThemes `year` and `sequence` fields without throwing,
  eliminating the per-series unexpected failures seen in the sweep log.

## 1.2.1.3

- Changed the AnimeThemes API identification header to a Cloudflare-compatible
  product user-agent, fixing the HTTP 403 response seen across the library.
- Temporary failure at one provider now falls through to the remaining
  independent providers. If none succeeds, the aggregate result stays transient
  so an incomplete lookup is never negative-cached.
- Reuses the existing Trailer Reel `yt-dlp` and configuration when present,
  retaining YoutubeExplode as the standalone fallback. This fixes the curated
  *A Knight of the Seven Kingdoms* ThemerrDB video timing out in YoutubeExplode.
- Added a per-run AnimeThemes outage circuit breaker and warning deduplication so
  one provider outage no longer creates hundreds of identical log warnings or
  repeated requests.

## 1.2.1.2

- Added `YoutubeExplode.dll` to the Jellyfin 12.1 local manifest assembly list,
  allowing the plugin loader to resolve the dependency during startup instead
  of marking the plugin `NotSupported`.
- Extended the packaging check to require declarations for both runtime
  assemblies.
- No provider, matching, normalization, or no-overwrite behavior changed from
  1.2.1.0.

## 1.2.1.1

- Added the Jellyfin 12.1 local `meta.json` manifest to the catalog ZIP with an
  explicit assembly declaration, preventing a successful install from
  disappearing after server restart.
- Added a packaging check that rejects nested, missing, or unexpected catalog
  ZIP contents before a release can be published.
- No provider, matching, normalization, or no-overwrite behavior changed from
  1.2.1.0.

## 1.2.1.0

- Allows a dead, restricted, streamless, timed-out, or unconvertible ThemerrDB
  YouTube candidate to fall through to AnimeThemes and Plex while genuine
  provider/API outages still stop the chain.
- Replaced YoutubeExplode's implicit 100-second request timeout with an explicit
  60-second total remote-candidate limit and selects the smallest suitable audio
  representation.
- Added safe support for extracting audio from a small muxed stream when YouTube
  exposes no audio-only representation.
- Starts a fresh negative-cache generation so misses recorded before AnimeThemes
  was introduced are checked against it immediately after upgrading.
- Added a final sweep summary covering downloads, confirmed misses, transient
  failures, write failures, unexpected errors, skips, and total series scanned.

## 1.2.0.0

- Changed provider priority to ThemerrDB first, strict AnimeThemes second, and
  Plex last.
- Added exact title/synonym and year validation for AnimeThemes and selects only
  a safe OP1 audio file from its trusted audio host.
- Blocked Plex's known incorrect `Barber of Seville` asset for *A Knight of the
  Seven Kingdoms* using both TVDB ID and content hash.
- Added configurable EBU R128 normalization for new downloads, defaulting to
  -18 LUFS integrated loudness with a -1.5 dBTP true-peak ceiling.
- Added provider, catalogue-ID, and source-URL audit logging for successful
  downloads.
- Preserved the no-overwrite rule: existing `theme.mp3` files are never changed.

## 1.1.0.0

- Updated the compile-time Jellyfin Controller and Model dependencies to 12.1.0.
- Raised the declared plugin target ABI to Jellyfin 12.1.0.0.
- Added an optional ThemerrDB fallback, matched by TMDB ID, after definitive
  Plex misses.
- Converts curated YouTube audio to a genuine MP3 using Jellyfin's configured
  FFmpeg binary, with source/output size limits and temporary-file cleanup.
- Stops the fallback chain on transient Plex failures so an outage does not
  multiply requests or produce misleading backoff records.
- Revalidated library scanning, MP3 safety checks, atomic writes, retry handling,
  item refreshes, cancellation, and Jellyfin 12.1 interface compatibility.

## 1.0.0.0

- Initial Jellyfin 12.0 release.
