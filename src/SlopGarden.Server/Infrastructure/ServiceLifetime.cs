using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using SlopGarden.Contracts;

namespace SlopGarden.Server.Infrastructure;

/// <summary>
/// The server process is a Windows service when the Service Control Manager starts it, and a systemd
/// service when a unit starts it. Each integration is a no-op on the other operating system.
/// </summary>
public static class MachineHost
{
    public static void Configure(IHostBuilder host)
    {
        host.UseWindowsService(o => o.ServiceName = MachineServices.ServerHostName);
        host.UseSystemd();
    }
}
