#!/bin/sh
# Installs the SlopGarden server as a systemd service that starts at boot.
# A packed release is this logic with the published server appended after the payload marker.
# The Electron setup app is not used.
#
#   sudo ./SlopGardenServer-linux-x64.sh --public-url http://192.168.1.20:5080
#   sudo ./SlopGardenServer-linux-x64.sh --uninstall
#
# --destdir writes the same files under a prefix and does not call systemctl (for tests).
set -eu

payload=""
public_url=""
password=""
no_start=false
uninstall=false
destdir=""
prefix="/opt/slopgarden-server"

while [ $# -gt 0 ]; do
  case "$1" in
    --payload) payload="${2:?--payload needs a directory}"; shift 2 ;;
    --public-url) public_url="${2:?--public-url needs a URL}"; shift 2 ;;
    --password) password="${2:?--password needs a value}"; shift 2 ;;
    --no-start) no_start=true; shift ;;
    --uninstall) uninstall=true; shift ;;
    --destdir) destdir="${2:?--destdir needs a directory}"; shift 2 ;;
    --prefix) prefix="${2:?--prefix needs a directory}"; shift 2 ;;
    -h|--help)
      echo "Usage: sudo $0 [--public-url URL] [--password PW] [--no-start] [--uninstall]"
      exit 0
      ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

if [ -z "$payload" ]; then
  payload=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
fi

root="${destdir}"
unit_name="slopgarden-server"
unit_path="${root}/etc/systemd/system/${unit_name}.service"
install_dir="${root}${prefix}"
data_dir="${root}/var/lib/slopgarden-server"
settings="${data_dir}/slopgarden.server.json"
exe="${install_dir}/SlopGarden.Server"

if [ -z "$password" ]; then
  password="slopgarden"
fi

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

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

write_unit() {
  mkdir -p "$(dirname "$unit_path")"
  cat >"$unit_path" <<EOF
[Unit]
Description=SlopGarden garden server
Wants=network-online.target
After=network-online.target

[Service]
Type=notify
StateDirectory=slopgarden-server
StateDirectoryMode=0750
WorkingDirectory=${prefix}
ExecStart=${prefix}/SlopGarden.Server
Restart=always
RestartSec=5
TimeoutStopSec=30
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1
Environment=SlopGarden__DataDirectory=/var/lib/slopgarden-server
Environment=ASPNETCORE_URLS=http://+:5080

NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=full

[Install]
WantedBy=multi-user.target
EOF
}

remove_service() {
  if [ -z "$destdir" ]; then
    if [ "$(id -u)" -ne 0 ]; then
      echo "Run as root to remove the systemd service." >&2
      exit 1
    fi
    systemctl disable --now "$unit_name" >/dev/null 2>&1 || true
  fi
  rm -f "$unit_path"
  if [ -z "$destdir" ]; then
    systemctl daemon-reload
  fi
  echo "Removed ${unit_name}."
}

if $uninstall; then
  remove_service
  exit 0
fi

if [ ! -x "$payload/SlopGarden.Server" ] && [ ! -f "$payload/SlopGarden.Server" ]; then
  echo "No SlopGarden.Server in $payload" >&2
  exit 1
fi

mkdir -p "$install_dir"
# Replace the published files but leave the installer script itself out of the tree.
find "$install_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
(cd "$payload" && find . -mindepth 1 -maxdepth 1 ! -name 'linux-install.sh' ! -name '*.sh' -exec cp -a {} "$install_dir"/ \;)
chmod 0755 "$exe" 2>/dev/null || chmod 0755 "$install_dir/SlopGarden.Server"
write_settings
write_unit

if [ -n "$destdir" ]; then
  echo "Staged ${unit_name} under $destdir (systemctl not called)."
  exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root to install the systemd service. Use --destdir to stage files without installing." >&2
  exit 1
fi

# A client service must not also be installed on this machine.
systemctl disable --now slopgarden-client >/dev/null 2>&1 || true
systemctl daemon-reload
systemctl enable "$unit_name" >/dev/null
if $no_start; then
  echo "Enabled ${unit_name}. Start it with: systemctl start ${unit_name}"
else
  systemctl restart "$unit_name"
  echo "Started ${unit_name}. It will start again when the machine boots."
fi
