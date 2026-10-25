using System.Diagnostics;

namespace SlopGarden.Setup;

static class Docker
{
    public static bool Available()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return proc is not null && proc.WaitForExit(4000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void Require()
    {
        if (!Available()) throw new InvalidOperationException("Docker is not installed or not running.");
    }

    public static async Task<string> ComposeUpAsync(string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker", "compose up -d")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("Could not start docker compose.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var log = (stdout + "\n" + stderr).Trim();
        if (proc.ExitCode != 0) throw new InvalidOperationException(log.Length > 0 ? log : "docker compose up failed.");
        return log;
    }
}
