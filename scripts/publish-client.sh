#!/usr/bin/env bash
# Publishes self-contained, single-file SlopGarden client builds into artifacts/client/<rid>/ (gitignored),
# together with the matching service installer from deploy/client/.
#
#   scripts/publish-client.sh                 # linux-arm64 (Raspberry Pi), linux-x64, win-x64, osx-arm64
#   scripts/publish-client.sh linux-arm64     # only the given runtime identifiers
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/SlopGarden.Client/SlopGarden.Client.csproj"
deploy="$root/deploy/client"
configuration="${CONFIGURATION:-Release}"

if [[ $# -gt 0 ]]; then
  rids=("$@")
else
  rids=(linux-arm64 linux-x64 win-x64 osx-arm64)
fi

for rid in "${rids[@]}"; do
  out="$root/artifacts/client/$rid"
  echo "==> $rid -> ${out#"$root"/}"
  rm -rf "$out"
  dotnet publish "$project" -c "$configuration" -r "$rid" --self-contained true -o "$out" \
    -p:PublishSingleFile=true -p:DebugType=embedded -nologo -v:quiet
  case "$rid" in
    linux-*)
      install -m 0755 "$deploy/install.sh" "$out/install.sh"
      install -m 0644 "$deploy/slopgarden-client.service" "$out/slopgarden-client.service"
      ;;
    win-*) cp "$deploy/install.ps1" "$out/" ;;
    osx-*) cp "$deploy/com.slopgarden.client.plist" "$out/" ;;
  esac
  echo "    $(du -sh "$out" | cut -f1) total, files: $(cd "$out" && ls | tr '\n' ' ')"
done
