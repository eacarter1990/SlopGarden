#!/bin/sh
# Proves the single-file Linux installer stages a boot-enabled systemd unit without calling systemctl.
set -eu
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
publish="$work/publish"
mkdir -p "$publish"
printf '%s\n' '#!/bin/sh' 'echo slopgarden-server' >"$publish/SlopGarden.Server"
chmod 0755 "$publish/SlopGarden.Server"
out="$work/SlopGardenServer-linux-test.sh"
"$root/scripts/pack-server-linux.sh" "$publish" "$out"
[ -x "$out" ]
dest="$work/root"
mkdir -p "$dest"
"$out" --destdir "$dest" --public-url "http://192.168.1.20:5080" --password 's3cret' --no-start
[ -x "$dest/opt/slopgarden-server/SlopGarden.Server" ]
unit="$dest/etc/systemd/system/slopgarden-server.service"
grep -q 'WantedBy=multi-user.target' "$unit"
grep -q 'Type=notify' "$unit"
grep -q 'Restart=always' "$unit"
grep -q 'ExecStart=/opt/slopgarden-server/SlopGarden.Server' "$unit"
grep -q 's3cret' "$dest/var/lib/slopgarden-server/slopgarden.server.json"
grep -q 'http://192.168.1.20:5080' "$dest/var/lib/slopgarden-server/slopgarden.server.json"
"$out" --destdir "$dest" --uninstall
[ ! -f "$unit" ]

mac="$work/mac"
mkdir -p "$mac"
"$root/deploy/server/mac-install.sh" --payload "$publish" --destdir "$mac" --password 's3cret' --public-url 'http://192.168.1.20:5080' --no-start
grep -q '<key>RunAtLoad</key>' "$mac/Library/LaunchDaemons/com.slopgarden.server.plist"
grep -q '<true/>' "$mac/Library/LaunchDaemons/com.slopgarden.server.plist"
grep -q 'com.slopgarden.server' "$mac/Library/LaunchDaemons/com.slopgarden.server.plist"
"$root/deploy/server/mac-install.sh" --destdir "$mac" --uninstall
[ ! -f "$mac/Library/LaunchDaemons/com.slopgarden.server.plist" ]

if [ -x /usr/bin/systemd-analyze ]; then
  sed 's#ExecStart=/opt/slopgarden-server/SlopGarden.Server#ExecStart=/bin/true#' \
    "$root/deploy/server/linux-install.sh" >/dev/null
  # Re-stage and verify the real unit text with an executable that exists.
  "$out" --destdir "$dest" --password 's3cret' --no-start
  verify="$work/slopgarden-server.service"
  sed 's#ExecStart=/opt/slopgarden-server/SlopGarden.Server#ExecStart=/bin/true#' \
    "$dest/etc/systemd/system/slopgarden-server.service" >"$verify"
  /usr/bin/systemd-analyze verify "$verify"
fi
grep -q 'Start="auto"' "$root/deploy/server/windows/Package.wxs"
grep -q 'Remove="uninstall"' "$root/deploy/server/windows/Package.wxs"
grep -q 'Name="SlopGardenServer"' "$root/deploy/server/windows/Package.wxs"
grep -q 'wix.exe' "$root/scripts/pack-server-msi.sh"
if grep -q 'wix heat' "$root/scripts/pack-server-msi.sh"; then
  echo "pack-server-msi.sh still calls wix heat, which WiX 5 removed" >&2
  exit 1
fi
bash -n "$root/scripts/pack-server-dmg.sh"
bash -n "$root/scripts/pack-server-msi.sh"
echo "server installer ok"
