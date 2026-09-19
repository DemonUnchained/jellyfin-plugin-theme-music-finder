#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$project_dir/artifacts/publish"
dist_dir="$project_dir/dist"
version="1.2.1.5"

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
  "$project_dir/src/Jellyfin.Plugin.ThemeMusicFinder/meta.json" \
  "$project_dir/LICENSE" \
  "$project_dir/THIRD_PARTY_NOTICES.md"

expected_contents="$(printf '%s\n' \
  'Jellyfin.Plugin.ThemeMusicFinder.dll' \
  'LICENSE' \
  'THIRD_PARTY_NOTICES.md' \
  'YoutubeExplode.dll' \
  'meta.json' | sort)"
actual_contents="$(unzip -Z1 "$archive" | sort)"

if [[ "$actual_contents" != "$expected_contents" ]]; then
  printf '%s\n' "Catalog ZIP has an invalid runtime layout:" "$actual_contents" >&2
  exit 1
fi

for assembly in Jellyfin.Plugin.ThemeMusicFinder.dll YoutubeExplode.dll; do
  if ! grep -Fq "\"$assembly\"" \
    "$project_dir/src/Jellyfin.Plugin.ThemeMusicFinder/meta.json"; then
    echo "meta.json does not declare $assembly." >&2
    exit 1
  fi
done

printf '%s\n' "$archive"
