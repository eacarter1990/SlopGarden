#!/bin/sh
# Builds one DMG containing the published server and the launchd installer.
# Run this on macOS (hdiutil). Linux release jobs do not produce a DMG.
#   scripts/pack-server-dmg.sh <publish-dir> <output.dmg>
set -eu
publish="${1:?publish directory}"
output="${2:?output .dmg}"
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
[ "$(uname -s)" = "Darwin" ] || { echo "A DMG has to be built on macOS." >&2; exit 1; }
[ -f "$publish/SlopGarden.Server" ] || { echo "missing $publish/SlopGarden.Server" >&2; exit 1; }
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/SlopGarden Server"
cp -a "$publish"/. "$stage/SlopGarden Server/"
cp "$root/deploy/server/mac-install.sh" "$stage/SlopGarden Server/mac-install.sh"
cp "$root/deploy/server/mac-install.sh" "$stage/Install SlopGarden Server.command"
chmod 0755 "$stage/SlopGarden Server/SlopGarden.Server" "$stage/Install SlopGarden Server.command"
rm -f "$output"
hdiutil create -volname "SlopGarden Server" -srcfolder "$stage" -ov -format UDZO "$output"
echo "Wrote $output"
