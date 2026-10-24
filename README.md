# SlopGarden

SlopGarden is a self-hosted garden automation server for your own LAN. A web app, a PostgreSQL database, and a small client on each Raspberry Pi (or any Linux machine) with your sensors, relays, and cameras. An AI assistant can propose changes; you approve them. Nothing here is exposed to the internet, and there is no TLS terminator in this release.

## Install

You need Docker with Compose, on a machine that can reach the Pis on your LAN.

```bash
git clone https://github.com/eacarter1990/SlopGarden.git
cd SlopGarden
docker compose up -d --build
```

Or download the setup app and run it on the machine. It asks whether this machine is the server or a client, and starts only that:

```bash
dotnet run --project tools/SlopGarden.Setup
```

It listens on http://127.0.0.1:5077. Server setup starts PostgreSQL and the web app. Client setup starts only the client and leaves the server address blank, so the client finds the server on the LAN (mDNS and UDP). Type an address only if discovery cannot see it. A client-only machine can also use `docker compose -f docker-compose.client.yml up -d`.

Open <http://localhost:5080>.

1. **First run.** The page asks for the Admin username and password (at least 8 characters). That is the only account created. There is no demo user and no demo password.
2. **Database.** Compose starts PostgreSQL next to the server and the server applies the latest schema on startup. To use a database you already run, open **Use a different PostgreSQL database** on that first page, enter the host, port, database, username, and password, and save. The server restarts onto that database, then you create the Admin. You can also set `POSTGRES_PASSWORD` before `docker compose up` if you only want a different password for the bundled database.
3. **Approve a client.** Start a client (below), or from the server open **Devices → Set up a machine by SSH**, enter the host, and confirm the host key. The login is not kept and is not visible to the AI. The machine still shows up as pending. An Admin clicks **Approve**. Until then it cannot read or change the garden. That machine needs Docker and an SSH login. A machine without SSH can run the setup app itself and choose Client.
4. **OpenAI key.** **Settings → AI models.** Add a provider, base URL `https://api.openai.com/v1`, and paste the API key. The key is stored encrypted. The same form is where LM Studio, Ollama, and other OpenAI-compatible servers go. Leave the key empty for a local server that does not need one.
5. **Discord.** **Settings → Notification channels.** Add a Discord webhook URL. Browser notifications are already on.
6. **Search.** **Settings → AI models**, in **Web search for the AI**. On `api.openai.com` the assistant uses OpenAI's own web search with the key from step 4. For anything else, pick SearXNG, Tavily, or Brave and enter that provider's key in the same form. Keys are not built into the server.

Logs (server, each client, and one activity log per garden, bed, and plant) are on the `serverdata` volume and under **Logs** in the app.

To point a Pi at this server, set `SLOPGARDEN_PUBLIC_URL` to the address the Pi can reach, for example `http://192.168.1.20:5080`, and run `docker compose up -d` again.

## Client

A client is a container or a published binary. With no server address configured, it finds the server on the LAN by mDNS (`_slopgarden-server._tcp`) and by UDP broadcast on port 47474. You can still add it by IP under **Devices**. It stays pending until you approve it. The client container uses host networking so those discovery packets reach the LAN.

### Container

```bash
docker compose --profile client up -d
```

That client uses the Compose network and `http://server:5080`. For a machine on the LAN, run the image yourself and set the server URL:

```bash
docker run -d --name slopgarden-client --restart unless-stopped \
  -e SLOPGARDEN_Client__ServerUrl=http://192.168.1.20:5080 \
  -e SLOPGARDEN_Client__DataDirectory=/data \
  -e SLOPGARDEN_CONTAINER=slopgarden-client \
  -v slopgarden-client:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --device /dev/video0 \
  ghcr.io/eacarter1990/slopgarden-client:latest
```

`--device /dev/video0` is only for a USB webcam. Drop that line if the machine has no camera. Raspberry Pi CSI cameras use the `libcamera` plugin when you configure one; USB webcams use the **USB webcam (V4L2)** plugin and need `ffmpeg`, which the image already includes.

Published images (linux/amd64 and linux/arm64) are on GHCR after a merge to `main`: `ghcr.io/<owner>/slopgarden-server` and `ghcr.io/<owner>/slopgarden-client`.

### Updates

**Devices** shows when a client image is older than the version you publish (**Client image** on that page: version and image reference, for example `ghcr.io/eacarter1990/slopgarden-client:0.2.3`). **Update** sends it to that device. **Auto** installs later images without another click.

Auto-update is only the client image. It does not approve a device, accept an AI proposal, or change a safety limit. Safety limits still run on the device.

A container can update itself only when the Docker socket is mounted and `SLOPGARDEN_CONTAINER` is the container's name, as in the `docker run` above. The server tells the client to pull the image; a short-lived helper container recreates it with the same mounts and restarts it. You do not open a shell on the device. A binary install (systemd on a Pi) is not replaced this way — use the container if you want updates from the device list.

### Binary on a Pi

```bash
scripts/publish-client.sh
scp -r artifacts/client/linux-arm64 pi@garden-pi:/tmp/slopgarden-client
ssh pi@garden-pi 'sudo /tmp/slopgarden-client/install.sh'
```

## Cameras

Each device has a list of cameras. A camera's role is **Garden**, **Bed**, or **Plant**, and it is attached to that garden, bed, or plant. The dashboard plays a live MJPEG stream through the server. A dense grid asks for a small width; a single plant asks for a large one.

## Growth reports

**Reports** generates a report now, or on a daily, weekly, or monthly schedule (07:00 in the garden's time zone). A report includes current conditions, harvest predictions, and the latest camera frames. If an AI model is configured for the Reports job (or for chat), it writes the narrative. Otherwise the server stores a structured report on its own.

## What the AI cannot do

The assistant cannot approve or revoke devices, manage users or personal access tokens, see or edit API keys, or change the database connection. Hydroponic dosing stays an approval: there is no "always allow" for a dose and no automatic dosing controller.

## Develop without Docker

.NET 10 SDK, Node 22, and PostgreSQL 16. Create the role and database `slopgarden` / `slopgarden`, then:

```bash
dotnet run --project src/SlopGarden.Server
dotnet test
```

The first visit still creates the Admin. `scripts/run-demo.sh` is a local demo with a seeded garden and a scripted model. It sets `SlopGarden__SeedDemo=true` and is not part of the install above.

Architecture notes live in [docs/architecture.md](docs/architecture.md). You do not need them to install.

## License

Apache License 2.0. See [LICENSE](LICENSE).
