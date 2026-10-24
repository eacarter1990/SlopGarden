#!/usr/bin/env bash
# Installs the SlopGarden client as a systemd service (Raspberry Pi OS 64-bit, Debian/Ubuntu and other systemd distros).
#
#   sudo ./install.sh [--source DIR] [--server URL] [--name NAME] [--enable-interfaces] [--no-start]
#
# --source             Publish output containing the slopgarden-client binary. Defaults to the directory of this
#                      script if it contains the binary, else artifacts/client/<rid> of the repository.
# --server             Server URL written to /etc/slopgarden/client.json (omit to use LAN auto-discovery).
# --name               Device name shown in the server (defaults to the host name).
# --enable-interfaces  On Raspberry Pi OS: enable I2C and 1-Wire with raspi-config (takes effect after a reboot).
# --no-start           Install and enable, but do not start the service now.
set -euo pipefail

SERVICE=slopgarden-client
SERVICE_USER=slopgarden
INSTALL_DIR=/opt/slopgarden-client
CONFIG_DIR=/etc/slopgarden
STATE_DIR=/var/lib/slopgarden
UNIT_FILE=/etc/systemd/system/${SERVICE}.service
GROUPS_NEEDED=(gpio i2c spi dialout bluetooth)

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_dir=""
server_url=""
device_name=""
enable_interfaces=false
start=true

die() { echo "error: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source) source_dir="${2:?--source needs a directory}"; shift 2 ;;
    --server) server_url="${2:?--server needs a URL}"; shift 2 ;;
    --name) device_name="${2:?--name needs a value}"; shift 2 ;;
    --enable-interfaces) enable_interfaces=true; shift ;;
    --no-start) start=false; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "run as root (sudo $0 ...)"
[[ "$server_url$device_name" != *[\"\\]* ]] || die "--server/--name must not contain quotes or backslashes"
command -v systemctl >/dev/null || die "systemd is required"

case "$(uname -m)" in
  aarch64|arm64) rid=linux-arm64 ;;
  x86_64|amd64) rid=linux-x64 ;;
  armv7l|armv6l) die "32-bit ARM is not supported; install Raspberry Pi OS (64-bit)" ;;
  *) die "unsupported architecture $(uname -m)" ;;
esac

if [[ -z "$source_dir" ]]; then
  if [[ -x "$script_dir/slopgarden-client" ]]; then
    source_dir="$script_dir"
  else
    source_dir="$(cd "$script_dir/../.." && pwd)/artifacts/client/$rid"
  fi
fi
[[ -x "$source_dir/slopgarden-client" ]] || die "no slopgarden-client binary in $source_dir (run scripts/publish-client.sh first or pass --source)"
unit_source="$script_dir/slopgarden-client.service"
[[ -f "$unit_source" ]] || unit_source="$source_dir/slopgarden-client.service"
[[ -f "$unit_source" ]] || die "slopgarden-client.service not found next to install.sh"

echo "Installing SlopGarden client ($rid) from $source_dir"

for group in "${GROUPS_NEEDED[@]}"; do
  getent group "$group" >/dev/null || groupadd --system "$group"
done
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  getent group "$SERVICE_USER" >/dev/null || groupadd --system "$SERVICE_USER"
  useradd --system --home-dir "$STATE_DIR" --no-create-home --shell /usr/sbin/nologin --gid "$SERVICE_USER" "$SERVICE_USER"
fi
usermod --append --groups "$(IFS=,; echo "${GROUPS_NEEDED[*]}")" "$SERVICE_USER"

if systemctl is-active --quiet "$SERVICE"; then
  echo "Stopping running service"
  systemctl stop "$SERVICE"
fi

# Replace the binaries but keep operator-installed plugins.
mkdir -p "$INSTALL_DIR"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name plugins -exec rm -rf {} +
(cd "$source_dir" && find . -mindepth 1 -maxdepth 1 ! -name plugins -exec cp -a {} "$INSTALL_DIR"/ \;)
mkdir -p "$INSTALL_DIR/plugins"
chown -R root:root "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR" "$INSTALL_DIR/slopgarden-client"
ln -sf "$INSTALL_DIR/slopgarden-client" /usr/local/bin/slopgarden-client

install -d -m 0755 "$CONFIG_DIR"
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_USER" "$STATE_DIR"
if [[ ! -f "$CONFIG_DIR/client.json" ]]; then
  cat >"$CONFIG_DIR/client.json" <<EOF
{
  "Client": {
    "ServerUrl": "${server_url}",
    "Name": "${device_name}"
  }
}
EOF
  chmod 0644 "$CONFIG_DIR/client.json"
  echo "Wrote $CONFIG_DIR/client.json"
elif [[ -n "$server_url" || -n "$device_name" ]]; then
  echo "note: $CONFIG_DIR/client.json exists and was not changed; edit it to set ServerUrl/Name"
fi

if $enable_interfaces; then
  if command -v raspi-config >/dev/null; then
    raspi-config nonint do_i2c 0
    raspi-config nonint do_onewire 0
    echo "Enabled I2C and 1-Wire (reboot to activate)"
  else
    echo "note: raspi-config not found; enable I2C/1-Wire manually"
  fi
fi

install -m 0644 "$unit_source" "$UNIT_FILE"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
if $start; then
  systemctl restart "$SERVICE"
  echo "Service started. Logs: journalctl -u $SERVICE -f    Status: slopgarden-client status"
else
  echo "Service enabled (not started). Start with: systemctl start $SERVICE"
fi
