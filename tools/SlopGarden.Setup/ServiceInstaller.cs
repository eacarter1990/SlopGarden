using System.Diagnostics;
using System.Runtime.InteropServices;
using SlopGarden.Contracts;

namespace SlopGarden.Setup;

/// <summary>Writes the unit or plist and registers it with the service manager for this operating system.</summary>
public static class ServiceInstaller
{
    public static ServicePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ServicePlatform.Windows :
        OperatingSystem.IsMacOS() ? ServicePlatform.MacOs :
        OperatingSystem.IsLinux() ? ServicePlatform.Linux :
        throw new InvalidOperationException("Setup can register a service on Windows, Linux, and macOS.");

    public static async Task<string> InstallAsync(ServicePlan plan, CancellationToken ct)
    {
        RequirePrivileged(plan.Platform);
        if (!File.Exists(plan.Executable))
            throw new InvalidOperationException($"Could not find {plan.Executable}. Publish the SlopGarden {(plan.Role == ServiceRole.Server ? "server" : "client")} into that folder first.");
        if (plan.Platform == ServicePlatform.Linux && plan.Role == ServiceRole.Client)
            await EnsureClientAccountAsync(ct);
        await RunAllAsync(plan.Prepare, ct);
        foreach (var file in plan.Files) WriteFile(file, secret: file.Contents.Contains("Password", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(plan.UnitPath)) WriteFile(new ServiceFile(plan.UnitPath, plan.UnitBody), secret: false);
        await RunAllAsync(plan.Activate, ct);
        return $"Registered {plan.Describe()} and set it to start at boot.";
    }

    public static async Task<string> RemoveAsync(ServicePlan plan, CancellationToken ct)
    {
        RequirePrivileged(plan.Platform);
        await RunAllAsync(plan.Stop, ct);
        if (!string.IsNullOrEmpty(plan.UnitPath) && File.Exists(plan.UnitPath)) File.Delete(plan.UnitPath);
        await RunAllAsync(plan.FinishRemoval, ct);
        return $"Removed {plan.Describe()}.";
    }

    private static void WriteFile(ServiceFile file, bool secret)
    {
        var dir = Path.GetDirectoryName(file.Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file.Path, file.Contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file.Path, secret ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static async Task EnsureClientAccountAsync(CancellationToken ct)
    {
        if (await RunAsync(["id", "slopgarden"], ignoreFailure: true, ct) == 0) return;
        foreach (var group in new[] { "gpio", "i2c", "spi", "dialout", "bluetooth", "slopgarden" })
            await RunAsync(["groupadd", "--system", group], ignoreFailure: true, ct);
        var code = await RunAsync(["useradd", "--system", "--home-dir", "/var/lib/slopgarden", "--shell", "/usr/sbin/nologin", "--gid", "slopgarden", "slopgarden"], ignoreFailure: true, ct);
        if (code != 0 && await RunAsync(["id", "slopgarden"], ignoreFailure: true, ct) != 0)
            throw new InvalidOperationException("Could not create the slopgarden service user.");
    }

    private static async Task RunAllAsync(IReadOnlyList<ServiceCommand> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            var code = await RunAsync(command.Arguments, command.IgnoreFailure, ct);
            if (code != 0 && !command.IgnoreFailure)
                throw new InvalidOperationException($"{command.Arguments[0]} failed ({code}): {string.Join(' ', command.Arguments)}");
        }
    }

    private static async Task<int> RunAsync(IReadOnlyList<string> arguments, bool ignoreFailure, CancellationToken ct)
    {
        var start = new ProcessStartInfo(arguments[0]) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in arguments.Skip(1)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {arguments[0]}.");
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 && !ignoreFailure && !string.IsNullOrWhiteSpace(stderr))
            throw new InvalidOperationException(stderr.Trim());
        return process.ExitCode;
    }

    private static void RequirePrivileged(ServicePlatform platform)
    {
        if (platform == ServicePlatform.Windows)
        {
            if (!IsWindowsAdmin()) throw new InvalidOperationException("Run SlopGarden Setup as Administrator to register the Windows service.");
            return;
        }
        if (geteuid() != 0) throw new InvalidOperationException("Run SlopGarden Setup as root to register the service.");
    }

    private static bool IsWindowsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}
