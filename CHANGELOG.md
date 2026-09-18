# Changelog

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
