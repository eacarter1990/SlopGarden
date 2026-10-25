using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopGarden.Contracts;

public enum ServiceRole { Server, Client }

public enum ServicePlatform { Windows, Linux, MacOs }

/// <summary>One process invocation. <see cref="IgnoreFailure"/> is for stopping a service that is not installed yet.</summary>
public sealed record ServiceCommand(IReadOnlyList<string> Arguments, bool IgnoreFailure = false);

public sealed record ServiceFile(string Path, string Contents);

/// <summary>
/// How setup registers the server or the client so it starts at boot. Docker and Compose are not part of this.
/// A machine gets one role: installing the server removes the client service, and the other way around.
/// </summary>
public sealed record ServicePlan(
    ServiceRole Role,
    ServicePlatform Platform,
    string Manager,
    string Name,
    string DisplayName,
    string Executable,
    string UnitPath,
    string UnitBody,
    IReadOnlyList<ServiceFile> Files,
    IReadOnlyList<ServiceCommand> Prepare,
    IReadOnlyList<ServiceCommand> Activate,
    IReadOnlyList<ServiceCommand> Stop,
    IReadOnlyList<ServiceCommand> FinishRemoval)
{
    public string Describe() => $"{Manager} service {Name}";
}

public static class MachineServices
{
    public const string ServerHostName = "SlopGarden Server";
    public const string ClientHostName = "SlopGarden Client";
    public const string ServerWindowsName = "SlopGardenServer";
    public const string ClientWindowsName = "SlopGardenClient";
    public const string ServerUnit = "slopgarden-server";
    public const string ClientUnit = "slopgarden-client";
    public const string ServerLaunchd = "com.slopgarden.server";
    public const string ClientLaunchd = "com.slopgarden.client";

    public static ServicePlan Install(ServiceRole role, ServicePlatform platform, string executable, string? serverUrl, string? clientName, string? publicUrl, string databasePassword)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("The service executable is required.");
        if (executable.Contains('"') || executable.Contains('\n') || executable.Contains('\r'))
            throw new ArgumentException("The executable path cannot contain quotes or line breaks.");
        if (role == ServiceRole.Client && string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentException("A client service needs the server address.");
        return platform switch
        {
            ServicePlatform.Windows => Windows(role, executable, serverUrl, clientName, publicUrl, databasePassword),
            ServicePlatform.Linux => Linux(role, executable, serverUrl, clientName, publicUrl, databasePassword),
            ServicePlatform.MacOs => Mac(role, executable, serverUrl, clientName, publicUrl, databasePassword),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
    }

    private static ServicePlan Windows(ServiceRole role, string executable, string? serverUrl, string? clientName, string? publicUrl, string databasePassword)
    {
        var server = role == ServiceRole.Server;
        var name = server ? ServerWindowsName : ClientWindowsName;
        var other = server ? ClientWindowsName : ServerWindowsName;
        var display = server ? ServerHostName : ClientHostName;
        var dataDir = server ? @"C:\ProgramData\SlopGarden\server" : @"C:\ProgramData\SlopGarden\client";
        var args = server
            ? new[] { "--urls", "http://+:5080", $"--SlopGarden:DataDirectory={dataDir}" }
            : new[] { "run" };
        var bin = "binPath= " + Quote(executable) + (args.Length == 0 ? "" : " " + string.Join(' ', args.Select(Quote)));
        var files = new List<ServiceFile>();
        if (server)
            files.Add(new(Path.Combine(dataDir, "slopgarden.server.json"), ServerSettings(publicUrl, databasePassword)));
        else
            files.Add(new(@"C:\ProgramData\SlopGarden\client.json", ClientSettings(serverUrl, clientName)));
        return new ServicePlan(role, ServicePlatform.Windows, "windows-service", name, display, executable, "", "", files,
            Prepare: [Sc("stop", other, ignore: true), Sc("delete", other, ignore: true), Sc("stop", name, ignore: true), Sc("delete", name, ignore: true)],
            Activate:
            [
                new(["sc.exe", "create", name, bin, "start= auto", $"DisplayName= {display}"]),
                new(["sc.exe", "description", name, server
                    ? "SlopGarden garden server."
                    : "SlopGarden garden client: sensors, actuators and cameras."]),
                new(["sc.exe", "failure", name, "reset= 86400", "actions= restart/5000/restart/5000/restart/5000"]),
                new(["sc.exe", "failureflag", name, "1"]),
                new(["sc.exe", "start", name]),
            ],
            Stop: [Sc("stop", name, ignore: true), Sc("delete", name, ignore: true)],
            FinishRemoval: []);
    }

    private static ServicePlan Linux(ServiceRole role, string executable, string? serverUrl, string? clientName, string? publicUrl, string databasePassword)
    {
        var server = role == ServiceRole.Server;
        var name = server ? ServerUnit : ClientUnit;
        var other = server ? ClientUnit : ServerUnit;
        var unitPath = $"/etc/systemd/system/{name}.service";
        var work = Path.GetDirectoryName(executable) ?? "/";
        var files = new List<ServiceFile>();
        string unit;
        if (server)
        {
            files.Add(new("/var/lib/slopgarden-server/slopgarden.server.json", ServerSettings(publicUrl, databasePassword)));
            unit = SystemdUnit(name, "SlopGarden garden server", executable, "", work, "/var/lib/slopgarden-server", server: true);
        }
        else
        {
            files.Add(new("/etc/slopgarden/client.json", ClientSettings(serverUrl, clientName)));
            unit = SystemdUnit(name, "SlopGarden garden client (sensors, actuators, cameras)", executable, "run", work, "/var/lib/slopgarden", server: false);
        }
        return new ServicePlan(role, ServicePlatform.Linux, "systemd", name, server ? ServerHostName : ClientHostName, executable, unitPath, unit, files,
            Prepare: [Systemctl(["disable", "--now", other], ignore: true), Systemctl(["disable", "--now", name], ignore: true)],
            Activate: [Systemctl(["daemon-reload"]), Systemctl(["enable", name]), Systemctl(["restart", name])],
            Stop: [Systemctl(["disable", "--now", name], ignore: true)],
            FinishRemoval: [Systemctl(["daemon-reload"])]);
    }

    private static ServicePlan Mac(ServiceRole role, string executable, string? serverUrl, string? clientName, string? publicUrl, string databasePassword)
    {
        var server = role == ServiceRole.Server;
        var label = server ? ServerLaunchd : ClientLaunchd;
        var other = server ? ClientLaunchd : ServerLaunchd;
        var plistPath = $"/Library/LaunchDaemons/{label}.plist";
        var work = Path.GetDirectoryName(executable) ?? "/";
        var data = server ? "/Library/Application Support/SlopGarden/server" : "/Library/Application Support/SlopGarden/client";
        var log = server ? "/Library/Logs/slopgarden-server.log" : "/Library/Logs/slopgarden-client.log";
        var args = server
            ? new[] { executable, "--urls", "http://+:5080", $"--SlopGarden:DataDirectory={data}" }
            : new[] { executable, "run", $"--Client:DataDirectory={data}" };
        var files = new List<ServiceFile>
        {
            server
                ? new(Path.Combine(data, "slopgarden.server.json"), ServerSettings(publicUrl, databasePassword))
                : new("/Library/Application Support/SlopGarden/client.json", ClientSettings(serverUrl, clientName)),
        };
        return new ServicePlan(role, ServicePlatform.MacOs, "launchd", label, server ? ServerHostName : ClientHostName, executable, plistPath,
            LaunchdPlist(label, args, work, log), files,
            Prepare: [Launchctl(["bootout", $"system/{other}"], ignore: true), Launchctl(["bootout", $"system/{label}"], ignore: true)],
            Activate: [Launchctl(["bootstrap", "system", plistPath]), Launchctl(["enable", $"system/{label}"]), Launchctl(["kickstart", "-k", $"system/{label}"])],
            Stop: [Launchctl(["bootout", $"system/{label}"], ignore: true)],
            FinishRemoval: []);
    }

    public static string SystemdUnit(string name, string description, string executable, string argument, string workingDirectory, string stateDirectory, bool server)
    {
        var exec = string.IsNullOrEmpty(argument) ? Quote(executable) : Quote(executable) + " " + argument;
        var user = server ? "" : """
            User=slopgarden
            Group=slopgarden
            SupplementaryGroups=gpio i2c spi dialout bluetooth

            """;
        var config = server ? "" : "ConfigurationDirectory=slopgarden\n";
        var state = server ? "slopgarden-server" : "slopgarden";
        var dataEnv = server
            ? $"Environment=SlopGarden__DataDirectory={stateDirectory}\n            Environment=ASPNETCORE_URLS=http://+:5080\n"
            : "Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/slopgarden/.bundle\n";
        return $"""
            [Unit]
            Description={description}
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=notify
            {user}StateDirectory={state}
            StateDirectoryMode=0750
            {config}WorkingDirectory={workingDirectory}
            ExecStart={exec}
            Restart=always
            RestartSec=5
            TimeoutStopSec=30
            Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
            Environment=DOTNET_NOLOGO=1
            {dataEnv}
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectHome=true
            ProtectSystem=full

            [Install]
            WantedBy=multi-user.target
            """;
    }

    public static string LaunchdPlist(string label, IReadOnlyList<string> arguments, string workingDirectory, string log)
    {
        var args = string.Join("\n", arguments.Select(a => $"        <string>{Xml(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Xml(label)}</string>
                <key>ProgramArguments</key>
                <array>
            {args}
                </array>
                <key>WorkingDirectory</key>
                <string>{Xml(workingDirectory)}</string>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ThrottleInterval</key>
                <integer>5</integer>
                <key>ExitTimeOut</key>
                <integer>30</integer>
                <key>StandardOutPath</key>
                <string>{Xml(log)}</string>
                <key>StandardErrorPath</key>
                <string>{Xml(log)}</string>
            </dict>
            </plist>
            """;
    }

    public static string ServerSettings(string? publicUrl, string databasePassword)
    {
        var section = new JsonObject { ["Database"] = new JsonObject { ["Password"] = databasePassword } };
        if (!string.IsNullOrWhiteSpace(publicUrl)) section["PublicUrl"] = publicUrl;
        return new JsonObject { ["SlopGarden"] = section }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ClientSettings(string? serverUrl, string? clientName) =>
        new JsonObject
        {
            ["Client"] = new JsonObject
            {
                ["ServerUrl"] = serverUrl ?? "",
                ["Name"] = clientName ?? "",
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static ServiceCommand Sc(string verb, string name, bool ignore) =>
        new(["sc.exe", verb, name], ignore);

    private static ServiceCommand Systemctl(IReadOnlyList<string> args, bool ignore = false) =>
        new(new[] { "systemctl" }.Concat(args).ToArray(), ignore);

    private static ServiceCommand Launchctl(IReadOnlyList<string> args, bool ignore = false) =>
        new(new[] { "launchctl" }.Concat(args).ToArray(), ignore);

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('\t') ? $"\"{value}\"" : value;

    private static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
