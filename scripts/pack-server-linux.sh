#!/bin/sh
# Packs a published SlopGarden server directory into one shell script.
#   scripts/pack-server-linux.sh <publish-dir> <output.sh>
set -eu
publish="${1:?publish directory}"
output="${2:?output .sh}"
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
installer="$root/deploy/server/linux-install.sh"
[ -d "$publish" ] || { echo "missing publish dir $publish" >&2; exit 1; }
[ -f "$publish/SlopGarden.Server" ] || { echo "missing $publish/SlopGarden.Server" >&2; exit 1; }
[ -f "$installer" ] || { echo "missing $installer" >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp -a "$publish"/. "$stage"/
cp "$installer" "$stage/linux-install.sh"
chmod 0755 "$stage/linux-install.sh" "$stage/SlopGarden.Server"

{
  cat <<'HEADER'
#!/bin/sh
# SlopGarden server installer. One file: this script, then the published server.
set -eu
marker="__SLOPGARDEN_SERVER_PAYLOAD__"
offset=$(grep -abo "^${marker}$" "$0" | head -n 1 | cut -d: -f1)
if [ -z "$offset" ]; then
  echo "This installer is missing its payload." >&2
  exit 1
fi
start=$((offset + ${#marker} + 1))
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
tail -c +$((start + 1)) "$0" | tar -xzf - -C "$tmp"
exec "$tmp/linux-install.sh" --payload "$tmp" "$@"
HEADER
  printf '%s\n' "__SLOPGARDEN_SERVER_PAYLOAD__"
  tar -C "$stage" -czf - .
} >"$output"
chmod 0755 "$output"
echo "Wrote $output"
