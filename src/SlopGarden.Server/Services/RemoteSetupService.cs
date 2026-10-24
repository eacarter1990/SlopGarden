using System.Collections.Concurrent;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using SlopGarden.Contracts;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed record RemoteSetupTarget(string Host, int Port, string Username, string? Password, string? PrivateKey);

public sealed record RemoteProbeResult(string SessionId, string Fingerprint, bool DockerInstalled, string ServerUrl, string Detail);

/// <summary>
/// Installs a client on another machine over SSH. The login is held only until the Admin confirms the host key,
/// then it is forgotten. The new client still registers as pending.
/// </summary>
public sealed class RemoteSetupService(ServerUrlProvider urls)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public async Task<RemoteProbeResult> ProbeAsync(RemoteSetupTarget target, CancellationToken ct)
    {
        var url = urls.Get();
        var (fingerprint, docker, detail) = await Task.Run(() => Probe(target), ct);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _pending[id] = new Pending(target, fingerprint, docker, DateTimeOffset.UtcNow + Lifetime);
        ForgetExpired();
        return new RemoteProbeResult(id, fingerprint, docker, url, detail);
    }

    public async Task<string> InstallAsync(string sessionId, string confirmedFingerprint, string clientName, string? image, CancellationToken ct)
    {
        if (!_pending.TryRemove(sessionId, out var pending) || pending.Expires < DateTimeOffset.UtcNow)
            throw AppException.Bad("That check expired. Check the machine again.");
        if (!string.Equals(pending.Fingerprint, confirmedFingerprint.Trim(), StringComparison.Ordinal))
            throw AppException.Bad("The confirmation did not match the host key. Nothing was installed.");
        if (!pending.DockerInstalled)
            throw AppException.Bad("Docker is not installed on that machine. Install Docker, or run slopgarden-setup there.");
        string script;
        try { script = MachineSetup.ClientDockerScript(null, clientName, image); }
        catch (ArgumentException ex) { throw AppException.Bad(ex.Message); }
        return await Task.Run(() => Run(pending.Target, pending.Fingerprint, script), ct);
    }

    public static (string Fingerprint, bool Docker, string Detail) Probe(RemoteSetupTarget target)
    {
        using var client = Connect(target, expectedFingerprint: null, out var fingerprint);
        var found = RunCommand(client, "command -v docker >/dev/null 2>&1 && echo yes || echo no").Trim();
        var docker = found == "yes";
        var detail = docker ? "Docker is installed." : "Docker is not installed on that machine.";
        return (fingerprint, docker, detail);
    }

    public static string Run(RemoteSetupTarget target, string expectedFingerprint, string script)
    {
        using var client = Connect(target, expectedFingerprint, out var actual);
        if (!string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
            throw AppException.Bad("The host key changed since the check. Nothing was installed.");
        return RunCommand(client, script);
    }

    private static SshClient Connect(RemoteSetupTarget target, string? expectedFingerprint, out string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(target.Host) || string.IsNullOrWhiteSpace(target.Username))
            throw AppException.Bad("Host and username are required.");
        if (string.IsNullOrWhiteSpace(target.Password) && string.IsNullOrWhiteSpace(target.PrivateKey))
            throw AppException.Bad("A password or a private key is required.");
        Renci.SshNet.ConnectionInfo info;
        try
        {
            info = string.IsNullOrWhiteSpace(target.PrivateKey)
                ? new PasswordConnectionInfo(target.Host.Trim(), target.Port <= 0 ? 22 : target.Port, target.Username.Trim(), target.Password ?? "")
                : new PrivateKeyConnectionInfo(target.Host.Trim(), target.Port <= 0 ? 22 : target.Port, target.Username.Trim(), new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(target.PrivateKey))));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw AppException.Bad("The private key could not be read.");
        }
        info.Timeout = TimeSpan.FromSeconds(12);
        var seen = "";
        var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            seen = MachineSetup.FingerprintSha256(e.HostKey);
            e.CanTrust = expectedFingerprint is null || seen == expectedFingerprint;
        };
        try
        {
            client.Connect();
        }
        catch (SshAuthenticationException)
        {
            client.Dispose();
            throw AppException.Bad("The login was rejected.");
        }
        catch (SshConnectionException ex)
        {
            client.Dispose();
            throw AppException.Bad(expectedFingerprint is null ? "Could not connect: " + ex.Message : "The host key did not match. Nothing was installed.");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            client.Dispose();
            throw AppException.Bad("Could not connect to " + target.Host + ".");
        }
        fingerprint = seen;
        return client;
    }

    private static string RunCommand(SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(5);
        var output = cmd.Execute();
        var err = cmd.Error;
        var text = (output + (string.IsNullOrWhiteSpace(err) ? "" : "\n" + err)).Trim();
        if (cmd.ExitStatus != 0)
            throw AppException.Bad(text.Length > 0 ? text : "The command failed.");
        return text;
    }

    private void ForgetExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pending)
            if (pair.Value.Expires < now) _pending.TryRemove(pair.Key, out _);
    }

    private sealed record Pending(RemoteSetupTarget Target, string Fingerprint, bool DockerInstalled, DateTimeOffset Expires);
}
