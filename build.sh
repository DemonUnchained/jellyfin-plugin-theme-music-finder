#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$project_dir/artifacts/publish"
dist_dir="$project_dir/dist"
version="1.2.0.0"

dotnet publish "$project_dir/src/Jellyfin.Plugin.ThemeMusicFinder/Jellyfin.Plugin.ThemeMusicFinder.csproj" \
  -c Release \
  -o "$publish_dir" \
  -p:Version="$version" \
  -p:AssemblyVersion="$version" \
  -p:FileVersion="$version"

mkdir -p "$dist_dir"
archive="$dist_dir/ThemeMusicFinder_${version}.zip"
rm -f "$archive"

zip -q -9 -j "$archive" \
  "$publish_dir/Jellyfin.Plugin.ThemeMusicFinder.dll" \
  "$publish_dir/YoutubeExplode.dll" \
  "$project_dir/LICENSE" \
  "$project_dir/THIRD_PARTY_NOTICES.md"

printf '%s\n' "$archive"
