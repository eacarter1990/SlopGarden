#!/usr/bin/env bash
# Builds the sample external plugin and stages it into a client plugins folder.
#
#   scripts/stage-sample-plugin.sh [--dest <plugins dir>] [--sign <private key file>]
#
# Default destination: ~/.slopgarden-client/plugins (the client's <data dir>/plugins).
# Without --sign the client loads it as an UNSIGNED plugin (with a warning). Create a key with
# `slopgarden-client keygen` and trust its public key (server catalog or Client:TrustedPluginKeys) to get 'signed'.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/samples/plugins/SlopGarden.Plugins.Sample.HttpJson/SlopGarden.Plugins.Sample.HttpJson.csproj"
DEST="${HOME}/.slopgarden-client/plugins"
KEY=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dest) DEST="$2"; shift 2 ;;
    --sign) KEY="$2"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

TARGET="$DEST/http-json-sensor"
rm -rf "$TARGET"
mkdir -p "$TARGET"
dotnet publish "$PROJECT" -c Release -o "$TARGET" --nologo -v quiet
rm -f "$TARGET"/*.pdb

if [[ -n "$KEY" ]]; then
  dotnet run --project "$ROOT/src/SlopGarden.Client" -c Release --no-launch-profile -- sign-plugin --key "$KEY" --dir "$TARGET"
fi

echo "Staged sample plugin in $TARGET"
ls -1 "$TARGET"
