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

## Plugins

Built in: `emulator-sensor`, `emulator-actuator`, `emulator-camera`, `gpio-digital-input`, `gpio-relay`,
`ads1115`, `ds18b20`, `bme280`, `arduino-serial-adc` (see [hardware/arduino-adc-bridge.md](hardware/arduino-adc-bridge.md)),
plus hardware discovery (I²C bus scan, 1-Wire probes, emulator inventory) reported to the server.

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
