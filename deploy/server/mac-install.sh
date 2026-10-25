#!/bin/sh
# Installs the SlopGarden server as a launchd daemon that starts at boot.
# Shipped inside the DMG. --destdir stages files and does not call launchctl.
set -eu

payload=""
public_url=""
password=""
no_start=false
uninstall=false
destdir=""
prefix="/usr/local/slopgarden-server"
label="com.slopgarden.server"

while [ $# -gt 0 ]; do
  case "$1" in
    --payload) payload="${2:?}"; shift 2 ;;
    --public-url) public_url="${2:?}"; shift 2 ;;
    --password) password="${2:?}"; shift 2 ;;
    --no-start) no_start=true; shift ;;
    --uninstall) uninstall=true; shift ;;
    --destdir) destdir="${2:?}"; shift 2 ;;
    --prefix) prefix="${2:?}"; shift 2 ;;
    -h|--help) echo "Usage: sudo $0 [--public-url URL] [--password PW] [--uninstall]"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

if [ -z "$payload" ]; then
  payload=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
fi
if [ -z "$password" ]; then
  password="slopgarden"
fi

root="${destdir}"
install_dir="${root}${prefix}"
data_dir="${root}/Library/Application Support/SlopGarden/server"
settings="${data_dir}/slopgarden.server.json"
plist="${root}/Library/LaunchDaemons/${label}.plist"

json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

write_settings() {
  mkdir -p "$data_dir"
  url_line=""
  if [ -n "$public_url" ]; then
    url_line=$(printf ',\n    "PublicUrl": "%s"' "$(json_escape "$public_url")")
  fi
  cat >"$settings" <<EOF
{
  "SlopGarden": {
    "Database": {
      "Password": "$(json_escape "$password")"
    }${url_line}
  }
}
EOF
  chmod 0600 "$settings"
}

write_plist() {
  mkdir -p "$(dirname "$plist")"
  cat >"$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${label}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${prefix}/SlopGarden.Server</string>
        <string>--SlopGarden:DataDirectory=/Library/Application Support/SlopGarden/server</string>
    </array>
    <key>WorkingDirectory</key>
    <string>${prefix}</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>5</integer>
    <key>StandardOutPath</key>
    <string>/Library/Logs/slopgarden-server.log</string>
    <key>StandardErrorPath</key>
    <string>/Library/Logs/slopgarden-server.log</string>
</dict>
</plist>
EOF
  chmod 0644 "$plist"
}

if $uninstall; then
  if [ -z "$destdir" ]; then
    [ "$(id -u)" -eq 0 ] || { echo "Run as root to remove the launchd service." >&2; exit 1; }
    launchctl bootout "system/${label}" >/dev/null 2>&1 || true
  fi
  rm -f "$plist"
  echo "Removed ${label}."
  exit 0
fi

[ -f "$payload/SlopGarden.Server" ] || { echo "No SlopGarden.Server in $payload" >&2; exit 1; }
mkdir -p "$install_dir"
find "$install_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
(cd "$payload" && find . -mindepth 1 -maxdepth 1 ! -name 'mac-install.sh' ! -name '*.sh' ! -name '*.command' -exec cp -a {} "$install_dir"/ \;)
chmod 0755 "$install_dir/SlopGarden.Server"
write_settings
write_plist

if [ -n "$destdir" ]; then
  echo "Staged ${label} under $destdir (launchctl not called)."
  exit 0
fi

[ "$(id -u)" -eq 0 ] || { echo "Run as root to install the launchd service." >&2; exit 1; }
launchctl bootout "system/com.slopgarden.client" >/dev/null 2>&1 || true
launchctl bootout "system/${label}" >/dev/null 2>&1 || true
launchctl bootstrap system "$plist"
launchctl enable "system/${label}"
if $no_start; then
  echo "Loaded ${label}. Start it with: launchctl kickstart system/${label}"
else
  launchctl kickstart -k "system/${label}"
  echo "Started ${label}. It will start again when the machine boots."
fi
