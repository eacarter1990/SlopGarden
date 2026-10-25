#!/bin/sh
# Builds one MSI that installs the SlopGarden server Windows service (start=auto, removed on uninstall).
# WiX builds the MSI database on any OS. The published files must be a win-* publish folder.
#   scripts/pack-server-msi.sh <publish-dir> <output.msi> [version]
set -eu
publish="${1:?publish directory}"
output="${2:?output .msi}"
version="${3:-0.2.0}"
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
wix="${WIX:-wix}"
command -v "$wix" >/dev/null 2>&1 || { echo "The WiX tool (wix) is required to build the MSI." >&2; exit 1; }
[ -f "$publish/SlopGarden.Server.exe" ] || { echo "missing $publish/SlopGarden.Server.exe" >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
settings="$stage/slopgarden.server.json"
cat >"$settings" <<'EOF'
{
  "SlopGarden": {
    "Database": {
      "Password": "slopgarden"
    }
  }
}
EOF
# Harvest every published file except the exe, which the service component installs.
harvest_src="$stage/harvest"
mkdir -p "$harvest_src"
cp -a "$publish"/. "$harvest_src"/
rm -f "$harvest_src/SlopGarden.Server.exe"
"$wix" heat dir "$harvest_src" \
  -cg Harvested \
  -dr INSTALLFOLDER \
  -scom -sreg -srd -ag \
  -var var.HarvestDir \
  -o "$stage/Harvest.wxs"
"$wix" build \
  "$root/deploy/server/windows/Package.wxs" \
  "$stage/Harvest.wxs" \
  -d "PublishDir=$publish" \
  -d "HarvestDir=$harvest_src" \
  -d "SettingsFile=$settings" \
  -d "Version=$version" \
  -o "$output"
echo "Wrote $output"
