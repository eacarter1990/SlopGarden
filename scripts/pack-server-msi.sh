#!/bin/sh
# Builds one MSI that installs the SlopGarden server Windows service (start=auto, removed on uninstall).
# Run on Windows, where WiX is supported. Set WIX to wix.exe, or have it on PATH.
#   scripts/pack-server-msi.sh <publish-dir> <output.msi> [version] [win-x64|win-arm64]
set -eu
publish="${1:?publish directory}"
output="${2:?output .msi}"
version="${3:-0.2.0}"
rid="${4:-win-x64}"
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"

case "$rid" in
  win-arm64) arch=arm64 ;;
  win-x64) arch=x64 ;;
  *) echo "unsupported rid $rid" >&2; exit 1 ;;
esac

# Git Bash on the Windows runner does not resolve `wix` from the .NET global tools folder.
# `command -v` also skips wix.exe unless PATHEXT is applied, so look for the file directly.
resolve_wix() {
  if [ -n "${WIX:-}" ] && [ -f "$WIX" ]; then
    printf '%s\n' "$WIX"
    return 0
  fi
  if command -v wix >/dev/null 2>&1; then
    command -v wix
    return 0
  fi
  if command -v wix.exe >/dev/null 2>&1; then
    command -v wix.exe
    return 0
  fi
  home="${HOME:-}"
  if [ -z "$home" ] && [ -n "${USERPROFILE:-}" ] && command -v cygpath >/dev/null 2>&1; then
    home=$(cygpath -u "$USERPROFILE")
  fi
  for candidate in "$home/.dotnet/tools/wix.exe" "$home/.dotnet/tools/wix"; do
    if [ -n "$home" ] && [ -f "$candidate" ]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

wix=$(resolve_wix) || { echo "The WiX tool (wix) is required to build the MSI." >&2; exit 1; }
[ -f "$publish/SlopGarden.Server.exe" ] || { echo "missing $publish/SlopGarden.Server.exe" >&2; exit 1; }

to_wix_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -m "$1"
  else
    printf '%s\n' "$1"
  fi
}

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
# The service component installs the exe. Harvest everything else so it is not listed twice.
harvest_src="$stage/harvest"
mkdir -p "$harvest_src"
cp -a "$publish"/. "$harvest_src"/
rm -f "$harvest_src/SlopGarden.Server.exe"

"$wix" build \
  "$(to_wix_path "$root/deploy/server/windows/Package.wxs")" \
  -d "PublishDir=$(to_wix_path "$publish")" \
  -d "HarvestDir=$(to_wix_path "$harvest_src")" \
  -d "SettingsFile=$(to_wix_path "$settings")" \
  -d "Version=$version" \
  -arch "$arch" \
  -o "$(to_wix_path "$output")"
echo "Wrote $output"
