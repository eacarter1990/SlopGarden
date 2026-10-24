# SlopGarden

Open-source, AI-run garden automation. A self-hosted server on your LAN talks to SlopGarden clients
(typically Raspberry Pis with sensors and relays), and an AI agent — any OpenAI-compatible model you
bring — sets up and operates the garden with you, asking for approval before it changes anything.

> Milestone 1: web app, server, Pi client, plugin frameworks, AI chat with approvals + MCP server,
> and a sensor emulator for testing without hardware.

## What's in the box

| Path | What |
|---|---|
| `src/SlopGarden.Server` | ASP.NET Core server: REST API, SignalR hubs, AI agent, MCP server (`/mcp`), safety engine, notifications, LAN discovery. Serves the web app. |
| `src/web` | React + TypeScript + Mantine web app |
| `src/SlopGarden.Client` | Client worker service (systemd / Windows service / console). Runs plugins, enforces safety limits locally, buffers offline. |
| `src/SlopGarden.Plugins.Abstractions` | Plugin SDK: sensor, actuator, camera and hardware-discovery interfaces |
| `src/SlopGarden.Plugins.Hardware` | GPIO digital input, relay, ADS1115, DS18B20, BME280, Arduino serial ADC |
| `src/SlopGarden.Plugins.Emulator` | Plugins that talk to the sensor emulator |
| `src/SlopGarden.Emulator` | Virtual garden: signal models, REST API to change any signal, physics coupling, static-image camera, control page |
| `src/SlopGarden.Contracts` | Device protocol, desired-config document, plugin signing |
| `tools/SlopGarden.DevLlm` | Scripted, offline OpenAI-compatible "model" for demos and tests |
| `samples/plugins` | Example external plugin (`http-json-sensor`) |
| `deploy/client` | systemd unit, Pi install script, Windows service and launchd files |
| `tests/` | xUnit tests for server and client |

## Quick start (desktop + emulator)

Requirements: .NET 10 SDK, Node 20+, PostgreSQL 14+.

```bash
# 1. Database (any reachable PostgreSQL; defaults below)
sudo -u postgres psql -c "CREATE USER slopgarden WITH PASSWORD 'slopgarden' CREATEDB;" -c "CREATE DATABASE slopgarden OWNER slopgarden;"

# 2. Everything at once: scripted demo model, emulator, server (with demo garden) and an emulated client
scripts/run-demo.sh            # then open http://localhost:5080  (admin / slopgarden)
```

Or run the pieces yourself:

```bash
dotnet run --project src/SlopGarden.Server        # http://localhost:5080, first visit creates the Admin
dotnet run --project src/SlopGarden.Emulator      # http://localhost:5090 (control page)
dotnet run --project src/SlopGarden.Client        # finds the server by UDP broadcast; approve it under Devices
```

Database connection: `SlopGarden:Database:{Host,Port,Database,Username,Password,SslMode,Extra}` in
`appsettings.json`, environment variables (`SlopGarden__Database__Host=…`) or **Settings → Server &
database** in the web app (Admins).

## Raspberry Pi client

```bash
scripts/publish-client.sh                       # artifacts/client/linux-arm64/
scp -r artifacts/client/linux-arm64 pi@garden-pi:/tmp/slopgarden-client
ssh pi@garden-pi 'sudo /tmp/slopgarden-client/install.sh'
```

See [docs/client.md](docs/client.md) for configuration, CLI verbs and the plugin authoring/signing guide,
and [docs/emulator.md](docs/emulator.md) for the emulator API.

## AI and MCP

Add any OpenAI-compatible endpoint (OpenAI, LM Studio, Ollama, OpenRouter…) under **Settings → AI**
and pick a model per job. Every operation in the app is a tool in one registry, used by the web UI, the
built-in assistant and the MCP server at `/mcp` (Streamable HTTP, personal access tokens with
`read` / `propose` / `write` scopes). The **MCP tools** page documents each tool's signature and output.

## Tests

```bash
dotnet test                                     # needs PostgreSQL reachable as slopgarden/slopgarden (or SLOPGARDEN_TEST_PG)
```

## License

Apache License 2.0 — see [LICENSE](LICENSE). "SlopGarden" and the signed plugin catalog are intended to
be protected by trademark, not by the license.
