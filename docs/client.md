# SlopGarden client

The client runs next to the hardware (typically a Raspberry Pi). It registers with the server, applies
the configuration the server pushes (sensors, actuators, cameras, schedules, safety limits), reads
sensors, switches actuators and uploads camera frames. Safety limits, interlocks and schedules are
enforced **on the client**, so they keep working while the server is down; readings are buffered on disk
(SQLite) and uploaded when the connection returns.

## Install on a Raspberry Pi

Requires a 64-bit OS (Raspberry Pi OS Bookworm 64-bit or Ubuntu); the binary is self-contained, no
.NET install needed.

```bash
# On a build machine with the .NET 10 SDK
scripts/publish-client.sh                       # all RIDs, or: scripts/publish-client.sh linux-arm64
scp -r artifacts/client/linux-arm64 pi@garden-pi:/tmp/slopgarden-client

# On the Pi
sudo /tmp/slopgarden-client/install.sh --enable-interfaces          # finds the server on the LAN
sudo /tmp/slopgarden-client/install.sh --server http://192.168.1.10:5080 --name "Greenhouse Pi"
```

`install.sh` creates the `slopgarden` system user (groups `gpio i2c spi dialout bluetooth`), installs to
`/opt/slopgarden-client` (keeping `plugins/`), links `/usr/local/bin/slopgarden-client`, writes
`/etc/slopgarden/client.json` if missing and enables the `slopgarden-client` systemd service
(`Type=notify`, restarts on failure). `--enable-interfaces` turns on I²C and 1-Wire via `raspi-config`
(reboot afterwards); `--no-start` installs without starting. Re-running it upgrades in place.

Then approve the device in the web app under **Devices**. If UDP broadcast does not reach the server
(other subnet, Wi-Fi isolation), either set `ServerUrl` or use **Add device by IP** in the web app, which
calls the client's local API on port 8787.

```bash
journalctl -u slopgarden-client -f      # logs
slopgarden-client status                # state, server, device id, applied config, plugins
```

Other platforms: `artifacts/client/win-x64/install.ps1` (Windows service `SlopGardenClient`, run as
Administrator), `artifacts/client/osx-arm64/com.slopgarden.client.plist` (launchd; instructions inside).
On a desktop you can also just run `dotnet run --project src/SlopGarden.Client`.

## Files

| Path (Linux service) | |
|---|---|
| `/etc/slopgarden/client.json` | Configuration (see below) |
| `/var/lib/slopgarden/identity.json` | Hardware id, server URL, device id and token (`0600`) |
| `/var/lib/slopgarden/config.json` | Last configuration received from the server; applied at startup before connecting |
| `/var/lib/slopgarden/outbox.db` | Offline buffer (readings, events, run reports) and actuator run ledger for daily limits |
| `/opt/slopgarden-client/plugins/`, `/var/lib/slopgarden/plugins/` | External plugins |

When not running as a service the data directory is `~/.slopgarden-client` (Windows service:
`%ProgramData%\SlopGarden\client`). Config files: `/etc/slopgarden/client.json`,
`/Library/Application Support/SlopGarden/client.json` (macOS), `%ProgramData%\SlopGarden\client.json`.

## Configuration

Sources, later wins: `appsettings.json` next to the binary → environment variables (`Client__ServerUrl`) →
the config file above → `SLOPGARDEN_` environment variables (`SLOPGARDEN_Client__ServerUrl`) →
command line (`--Client:ServerUrl=http://…`).

```json
{
  "Client": {
    "ServerUrl": "http://192.168.1.10:5080",
    "Name": "Greenhouse Pi",
    "Location": { "Latitude": 52.37, "Longitude": 4.9, "Label": "Amsterdam" }
  }
}
```

| Key | Default | |
|---|---|---|
| `Client:ServerUrl` | *(empty)* | Server base URL. Empty: discover via UDP broadcast on port 47474 or wait for pairing by IP. When set, pairing from the web app is refused. |
| `Client:Name` | host name | Display name shown in the web app |
| `Client:DataDirectory` | see above | Identity, cached config, offline buffer |
| `Client:LocalApiPort` | `8787` | Local status/pairing API; `0` disables it |
| `Client:PluginsDirectory` | `<binary dir>/plugins` | Extra external plugins folder (`<data dir>/plugins` is always scanned) |
| `Client:TrustedPluginKeys` | `[]` | Plugin signing keys trusted in addition to those the server pushes |
| `Client:Location` | *(none)* | `Latitude`, `Longitude`, `Label`; reported to the server with the device's time zone |
| `Client:HardwareId` | Pi serial / `/etc/machine-id` / Windows MachineGuid | Override, e.g. to run several clients on one machine |
| `Client:DiscoveryPort` | `47474` | UDP discovery port |
| `Plugins:Emulator:BaseUrl` | `http://localhost:5090` | Sensor emulator used by the `emulator-*` plugins |
| `Plugins:Ds18b20:RootPath` | `/sys/bus/w1/devices` | 1-Wire sysfs directory |
| `Logging:LogLevel:Default` | `Information` | Standard .NET logging levels |

Everything else (sensors, actuators, schedules, limits, sampling intervals, time zone) comes from the server.

## CLI

```
slopgarden-client [run]                       run the client (console, systemd or Windows service)
slopgarden-client status                      status from the running client, else from disk
slopgarden-client pair --server <url> [--force]
                                              pair with a server; --force replaces an existing pairing
slopgarden-client reset                       forget the registration and device token (cached config and buffer stay)
slopgarden-client plugins                     list plugins, versions and trust levels
slopgarden-client keygen [--out <name>]       create a plugin signing key pair (<name>.key / <name>.pub)
slopgarden-client sign-plugin --key <file> --dir <plugin folder>
slopgarden-client version
```

Every verb accepts configuration overrides (`--Client:DataDirectory=/tmp/c2`). Run `status`, `pair`,
`reset` and `plugins` as root or `slopgarden` on a Pi (the state directory is `0750`); `reset` takes effect
after `systemctl restart slopgarden-client`.

### Local API (port 8787)

| Request | |
|---|---|
| `GET /status` | Hardware id, name, version, connection state, server URL, device id, applied config version, plugins |
| `POST /pair` `{"serverUrl": "http://…:5080"}` | 202: registers with that server. 409 if `Client:ServerUrl` is set or the device is already approved by another server (re-pair from the device itself: `pair --force` or `reset`). |

## Safety behaviour

- Every actuator command (from the server, the AI, a schedule) is checked locally: enabled, emergency stop,
  already running, interlocks, minimum interval between runs, maximum run time (pulses are capped,
  `on` gets an auto-off timer), daily run seconds and daily millilitres (counted in the configured time zone).
  Doses that would exceed a limit are rejected, not shortened.
- Interlocks fail closed: a missing reading, an error, or a reading older than 15 minutes blocks the
  actuator. A tripped interlock switches running actuators off.
- Actuators go to their failsafe state (normally off) at startup, at shutdown, when their hardware
  settings change and on emergency stop; disabling or removing an actuator switches it off. If switching
  off fails, the command is reported as failed and the client keeps retrying in the background.
- Schedules (cron, in the garden's time zone) run from the cached config while offline.
- Plugin exceptions are logged and reported as sensor/actuator errors; they never stop the client.
- Safety events carry a machine-readable `code` where one applies: `interlock_tripped`, `flow_stall`,
  `unexpected_flow`, `pump_not_running`.

### Hydroponics

Everything below runs on the device from the cached config, so it keeps working while offline. A
supervisor loop evaluates it about once a second.

- **Continuous runs** (`continuousRun: true`, e.g. NFT/DWC circulation pumps and air pumps): switched on
  when the config is applied (also at startup from the cached config) and re-asserted every 30 s while they
  are off and nothing blocks them (emergency stop, interlock, flow-stall wait, disabled, explicit off). The
  run is reported with source `continuous`; when it ends (off command, interlock, stall, emergency stop,
  reconfiguration) an actuator run is reported as usual. If a continuous run cannot be started, a
  `pump_not_running` warning is raised (at most every 30 min per actuator). Turning `continuousRun` off in
  the config stops a running continuous run.
- **Off latch**: an explicit `off` (server command or local API) keeps a continuous actuator off. The latch
  is cleared by an explicit `on`/`pulse`/`dose`, or by a config change *of that actuator* (any field, e.g.
  toggling `continuousRun` or renaming it) that still has `continuousRun: true`. A new config version that
  leaves the actuator unchanged, re-applying the same version (reconnects) and schedules do not clear it.
  The latch is kept in memory: after a client restart continuous actuators start again — disable the
  actuator to keep it off permanently.
- **Continuous roles** (`circulation_pump`, `air_pump`) ignore the per-run, daily and minimum-interval
  limits (they would otherwise stop a pump that is meant to run all day). Interlocks, emergency stop and
  `enabled` still apply.
- **Flow watchdog** (`flowWatch` on a pump or valve, pointing at a flow sensor channel in L/min):
  - *Stall*: on for at least `graceSec`, and the latest flow reading (taken after the grace period, at most
    60 s old) is below `minFlowWhenOn` → `critical` safety event `flow_stall` (at most every 5 min per
    actuator), e.g. "NFT pump: no flow (0.0 L/min) for 21 s — pump stopped, clogged or reservoir empty".
    With `stopOnStall` the actuator is switched off (run stop reason `flow_stall`); a continuous run then
    waits `stallRetrySec` (default 300 s) before it tries again, and keeps retrying once per
    `stallRetrySec` while the stall persists. Any explicit start clears the wait; sufficient flow clears it too.
  - *Unexpected flow*: off for at least `graceSec`, and a fresh reading taken after the grace period is above
    `maxFlowWhenOff` → `warning` event `unexpected_flow` (leak, stuck valve), at most every 5 min.
  - *Silent sensor*: on for more than 3 × `graceSec` without a fresh, valid flow reading → `warning`
    event (no stop, no code) once per silence, repeated hourly. Read flow sensors at least every 60 s
    (5–10 s is better).
- **Dosing limits** (checked for `on`, `pulse` and `dose`; `off` is always allowed):
  - `maxHourlyAmountMl`: millilitres in the last 60 minutes, including the request (recorded amounts, or
    run time × `flowRateMlPerMin`). Requires `flowRateMlPerMin`; an open-ended `on` is capped to what is left.
  - `minSecondsBetweenReservoirDoses`: rejected while any dosing actuator (`dose_*` role) with the same
    `reservoirId` is dosing or finished less than that many seconds ago, so the last dose can mix.
  - `requiresActuatorOn`: rejected unless that actuator is on (never dose while the circulation pump is
    off). If it goes off while this actuator runs, this one is switched off within a second
    (`critical` event `pump_not_running`, run stop reason `interlock`).

## Plugins

Built in: `emulator-sensor`, `emulator-actuator`, `emulator-camera`, `gpio-digital-input`, `gpio-relay`,
`gpio-flow-meter`, `ads1115`, `ds18b20`, `bme280`, `arduino-serial-adc` (see [hardware/arduino-adc-bridge.md](hardware/arduino-adc-bridge.md)),
plus hardware discovery (I²C bus scan, 1-Wire probes, emulator inventory) reported to the server.

`gpio-flow-meter` reads a hall-effect pulse flow meter (YF-S201 and similar) on a GPIO pin: it counts
falling edges and reports channel `flow` as `flow_l_min` = pulses ÷ `windowSec` ÷ `pulsesPerLiter` × 60.
Settings: `pin` (BCM, required), `pulsesPerLiter` (default 450, i.e. F[Hz] = 7.5 × Q[L/min]), `pullUp`
(default true; the sensor output is open-collector), `windowSec` (rolling window, default 2). Most of these
sensors run on 5 V: feed the signal to the Pi through a level shifter or a voltage divider.

Analog pH and EC probes (with their usual amplifier boards) go through `ads1115` (or `arduino-serial-adc`)
with `two_point` calibration on the channel: measure the raw voltage in two buffer/calibration solutions
(e.g. pH 4.0 and 7.0, or 1.413 and 12.88 mS/cm) and enter them as `rawLow`/`valueLow` and
`rawHigh`/`valueHigh`. Recalibrate pH probes monthly; EC readings depend on water temperature unless the
board compensates for it.

### Writing a plugin

An external plugin is a folder with `plugin.json`, the entry assembly and its dependencies. The sample
[`samples/plugins/SlopGarden.Plugins.Sample.HttpJson`](../samples/plugins/SlopGarden.Plugins.Sample.HttpJson)
reads a number from any JSON endpoint.

1. Create a `net10.0` class library with `<EnableDynamicLoading>true</EnableDynamicLoading>` and reference
   `SlopGarden.Plugins.Abstractions` with `Private="false" ExcludeAssets="runtime"` (the client provides it;
   a second copy would break type identity).
2. Implement one or more public classes with a parameterless constructor:
   - `ISensorPlugin` → `ISensor.ReadAsync` returns `RawReading(channelKey, value)` per channel declared by
     `DescribeChannels`; the client applies calibration and range checks.
   - `IActuatorPlugin` → `IActuator.SetStateAsync(on, level)`; declare `SupportedKinds`. Never switch
     anything on in `Create`; the client decides the initial (failsafe) state.
   - `ICameraPlugin` → `ICamera.CaptureAsync(width, height)` returns a JPEG/PNG `CameraFrame`.
   - `IHardwareDiscovery` → `DiscoverAsync` suggests devices with ready-to-use settings.

   `SettingsSchema` (JSON Schema, build it with `SchemaBuilder`) is shown to users and the AI when adding a
   component; `PluginContext.Settings` holds the values. Throw from `Create`/`ReadAsync` on errors — the
   client catches, logs and reports them. Dispose hardware handles in `DisposeAsync`.
3. Add `plugin.json` (copied to the output):

   ```json
   {
     "id": "http-json-sensor",
     "version": "1.0.0",
     "displayName": "HTTP JSON sensor (sample)",
     "entryAssembly": "SlopGarden.Plugins.Sample.HttpJson.dll",
     "publisher": "SlopGarden samples",
     "description": "Reads a number from any JSON HTTP endpoint.",
     "kinds": [ "sensor" ]
   }
   ```

4. `dotnet publish -c Release -o <plugins dir>/<id>` and restart the client. Each plugin gets its own
   `AssemblyLoadContext`, so its dependencies may differ from other plugins'.
   `scripts/stage-sample-plugin.sh [--dest <plugins dir>] [--sign <key>]` does this for the sample.

### Signing and trust

| Trust | Meaning |
|---|---|
| `builtin` | Ships with the client |
| `signed` | `plugin.sig.json` verifies against a trusted, non-revoked key and the entry assembly hash matches |
| `unsigned` | No signature, or signed with an unknown key. **Loaded with a warning**; shown as unsigned in the web app |
| `invalid` | Assembly modified after signing, bad signature or revoked key. **Not loaded** |

```bash
slopgarden-client keygen --out acme-plugins          # acme-plugins.key (keep secret) + acme-plugins.pub
slopgarden-client sign-plugin --key acme-plugins.key --dir /var/lib/slopgarden/plugins/http-json-sensor
```

The signature covers plugin id, version and the SHA-256 of the entry assembly (ECDSA P-256), so sign
after the final build and re-sign for every version. Trust the key by registering the public key on the
server (pushed to all clients) or locally:

```json
{
  "Client": {
    "TrustedPluginKeys": [
      { "keyId": "3f2a…", "publisher": "Acme", "publicKeySpki": "MFkwEwYHKoZIzj0CAQ…", "revoked": false }
    ]
  }
}
```

`keygen` prints this entry. `slopgarden-client plugins` shows the resulting trust level and why a plugin
was refused.
