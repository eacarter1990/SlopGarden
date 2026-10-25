using System.Text.RegularExpressions;

namespace SlopGarden.Contracts;

/// <summary>Files and commands the server's SSH installer uses. A client install never starts the server or PostgreSQL.</summary>
public static class MachineSetup
{
    public const string DefaultClientImage = "ghcr.io/eacarter1990/slopgarden-client:latest";
    public const string ClientContainer = "slopgarden-client";
    public const int SetupWizardPort = 5077;

    public static string ClientDockerScript(string? serverUrl, string clientName, string? image)
    {
        var url = string.IsNullOrWhiteSpace(serverUrl) ? null : RequireServerUrl(serverUrl);
        var name = RequireClientName(clientName);
        var img = string.IsNullOrWhiteSpace(image) ? DefaultClientImage : RequireImage(image);
        var serverLine = url is null ? "" : $"  -e SLOPGARDEN_Client__ServerUrl={Quote(url)} \\\n";
        var found = url is null
            ? "It finds the server on the LAN (mDNS and UDP) and stays pending until an Admin approves it."
            : "It stays pending until an Admin approves it on the server.";
        return $$"""
            set -eu
            if ! command -v docker >/dev/null 2>&1; then
              echo "Docker is not installed on this machine. Install Docker, then run this again."
              exit 2
            fi
            docker rm -f {{ClientContainer}} >/dev/null 2>&1 || true
            docker run -d --name {{ClientContainer}} --restart unless-stopped --network host \
            {{serverLine}}  -e SLOPGARDEN_Client__Name={{Quote(name)}} \
              -e SLOPGARDEN_Client__DataDirectory=/data \
              -e SLOPGARDEN_CONTAINER={{ClientContainer}} \
              -v {{ClientContainer}}:/data \
              -v /var/run/docker.sock:/var/run/docker.sock \
              {{img}}
            echo "Client container started. {{found}}"
            """;
    }

    /// <summary>Compose file for a machine that is only the server. No client service.</summary>
    public static string ServerCompose(string postgresPassword, string? publicUrl)
    {
        var password = string.IsNullOrWhiteSpace(postgresPassword) ? "slopgarden" : RequireSecret(postgresPassword, "PostgreSQL password");
        var url = string.IsNullOrWhiteSpace(publicUrl) ? "" : RequireServerUrl(publicUrl);
        return $$"""
            # This machine is the server.
            services:
              db:
                image: postgres:16
                environment:
                  POSTGRES_USER: slopgarden
                  POSTGRES_PASSWORD: "{{password}}"
                  POSTGRES_DB: slopgarden
                volumes:
                  - pgdata:/var/lib/postgresql/data
                healthcheck:
                  test: ["CMD-SHELL", "pg_isready -U slopgarden -d slopgarden"]
                  interval: 5s
                  timeout: 5s
                  retries: 20
                restart: unless-stopped
              server:
                image: ghcr.io/eacarter1990/slopgarden-server:latest
                container_name: slopgarden-server
                ports:
                  - "5080:5080"
                environment:
                  SLOPGARDEN_CONTAINER: slopgarden-server
                  SlopGarden__Database__Host: db
                  SlopGarden__Database__Password: "{{password}}"
                  SlopGarden__DataDirectory: /data
                  SlopGarden__SeedDemo: "false"
                  SlopGarden__PublicUrl: "{{url}}"
                volumes:
                  - serverdata:/data
                  - /var/run/docker.sock:/var/run/docker.sock
                depends_on:
                  db:
                    condition: service_healthy
                restart: unless-stopped
            volumes:
              pgdata:
              serverdata:
            """;
    }

    /// <summary>Compose file for a machine that is only a client.</summary>
    public static string ClientCompose(string? serverUrl, string clientName, string? image)
    {
        var url = string.IsNullOrWhiteSpace(serverUrl) ? null : RequireServerUrl(serverUrl);
        var name = RequireClientName(clientName);
        var img = string.IsNullOrWhiteSpace(image) ? DefaultClientImage : RequireImage(image);
        var serverLine = url is null ? "" : $"      SLOPGARDEN_Client__ServerUrl: \"{url}\"\n";
        return $$"""
            # This machine is a client. The server runs somewhere else.
            # With no server address, the client finds it on the LAN (mDNS and UDP). Host networking lets those packets through.
            services:
              client:
                image: {{img}}
                container_name: {{ClientContainer}}
                network_mode: host
                environment:
            {{serverLine}}      SLOPGARDEN_Client__Name: "{{name}}"
                  SLOPGARDEN_Client__DataDirectory: /data
                  SLOPGARDEN_CONTAINER: {{ClientContainer}}
                volumes:
                  - clientdata:/data
                  - /var/run/docker.sock:/var/run/docker.sock
                restart: unless-stopped
            volumes:
              clientdata:
            """;
    }

    public static string FingerprintSha256(byte[] hostKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    public static string RequireServerUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Server address must be an absolute http URL, for example http://192.168.1.20:5080");
        return uri.ToString().TrimEnd('/');
    }

    public static string RequireClientName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 40 || trimmed.Any(c => c is '\n' or '\r' or '\'' or '"'))
            throw new ArgumentException("Client name must be 1–40 characters without quotes.");
        return trimmed;
    }

    public static string RequireImage(string image)
    {
        var trimmed = image.Trim();
        if (!ImagePattern.IsMatch(trimmed))
            throw new ArgumentException("Image reference looks wrong. Use a name like ghcr.io/owner/slopgarden-client:latest");
        return trimmed;
    }

    public static string RequireSecret(string value, string label)
    {
        if (value.Any(c => c is '\n' or '\r' or '\'' or '"'))
            throw new ArgumentException($"{label} cannot contain quotes or line breaks.");
        return value;
    }

    /// <summary>Reads a server URL out of a discovery packet. Used by setup so a typed address is only needed when nothing answers.</summary>
    public static string? ServerUrlFromUdp(byte[] payload)
    {
        try
        {
            var announcement = System.Text.Json.JsonSerializer.Deserialize<DiscoveryAnnouncement>(payload, SlopJson.Options);
            if (announcement is null || announcement.Type != Discovery.ServerMessageType) return null;
            return NormalizeHttp(announcement.ServerUrl);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string? ServerUrlFromMdns(byte[] packet)
    {
        if (!MdnsCodec.TryRead(packet, out var message)) return null;
        foreach (var service in message.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Url)) continue;
            if (!service.ServiceType.Contains("slopgarden-server", StringComparison.OrdinalIgnoreCase)) continue;
            var url = NormalizeHttp(service.Url);
            if (url is not null) return url;
        }
        return null;
    }

    private static string? NormalizeHttp(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return uri.ToString().TrimEnd('/');
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static readonly Regex ImagePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,200}$", RegexOptions.CultureInvariant);
}
