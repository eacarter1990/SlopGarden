#!/usr/bin/env bash
# Starts the full SlopGarden demo stack in tmux sessions:
#   sg-llm      scripted OpenAI-compatible demo model   http://127.0.0.1:5199/v1
#   sg-emulator sensor emulator + control page          http://localhost:5090
#   sg-server   server + web app (demo garden seeded)   http://localhost:5080
#   sg-client   SlopGarden client using emulator plugins (finds the server by UDP broadcast)
# then approves the client and wires the emulated hardware into the demo garden.
#
# Env: SG_DEMO_DIR (state, default ~/.slopgarden-demo), SG_RESET=1 (wipe state + recreate the database)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEMO="${SG_DEMO_DIR:-$HOME/.slopgarden-demo}"
TMUX_CONF=/exec-daemon/tmux.portal.conf
tmx() { if [[ -f $TMUX_CONF ]]; then tmux -f "$TMUX_CONF" "$@"; else tmux "$@"; fi; }

if [[ "${SG_RESET:-0}" == "1" ]]; then
  for s in sg-client sg-server sg-emulator sg-llm; do tmx kill-session -t "$s" 2>/dev/null || true; done
  rm -rf "$DEMO"
  sudo -u postgres psql -qc "DROP DATABASE IF EXISTS slopgarden WITH (FORCE)" -c "CREATE DATABASE slopgarden OWNER slopgarden"
fi
mkdir -p "$DEMO"

echo "Building…"
(cd "$ROOT/src/web" && { [[ -d node_modules ]] || npm ci; } && npm run build >/dev/null)
dotnet build "$ROOT/SlopGarden.slnx" -c Release -p:SkipWebBuild=true -v q -nologo >/dev/null

start() {
  local name=$1; shift
  tmx kill-session -t "$name" 2>/dev/null || true
  tmx new-session -d -s "$name" -c "$ROOT" "$* 2>&1 | tee $DEMO/$name.log"
}

start sg-llm "dotnet run --no-build -c Release --project tools/SlopGarden.DevLlm --urls http://127.0.0.1:5199"
start sg-emulator "dotnet run --no-build -c Release --project src/SlopGarden.Emulator --urls http://0.0.0.0:5090"
start sg-server "SlopGarden__SeedDemo=true SlopGarden__InitialAdminPassword=slopgarden SlopGarden__DemoPassword=slopgarden \
  SlopGarden__DemoAiBaseUrl=http://127.0.0.1:5199/v1 SlopGarden__DataDirectory=$DEMO/server \
  dotnet run --no-build -c Release --no-launch-profile --project src/SlopGarden.Server"

until curl -sf http://localhost:5080/api/health >/dev/null; do sleep 1; done
until curl -sf http://localhost:5090/api/sensors >/dev/null; do sleep 1; done

start sg-client "Client__DataDirectory=$DEMO/client Client__HardwareId=demo-pi-1 Client__Name='Demo Pi (emulated)' \
  Client__Location__Latitude=52.37 Client__Location__Longitude=4.9 Client__Location__Label='Amsterdam, NL' \
  Plugins__Emulator__BaseUrl=http://localhost:5090 dotnet run --no-build -c Release --project src/SlopGarden.Client"

python3 "$ROOT/scripts/seed-demo.py" --server http://localhost:5080 --password slopgarden

cat <<EOF

SlopGarden demo is running.
  Web app   http://localhost:5080   admin / slopgarden  (also grower / slopgarden = ReadWrite, viewer / slopgarden = ReadOnly)
  Emulator  http://localhost:5090
  Logs      $DEMO/*.log   ·   tmux sessions: sg-llm sg-emulator sg-server sg-client
EOF
