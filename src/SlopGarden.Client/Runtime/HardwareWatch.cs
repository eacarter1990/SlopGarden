using SlopGarden.Client.Server;

namespace SlopGarden.Client.Runtime;

/// <summary>Looks again for hardware a loaded plugin can use, so a webcam or probe plugged in after startup is reported.</summary>
public sealed class HardwareWatch(HardwareDiscoveryRunner discovery, IServerLink link, ILogger<HardwareWatch> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!link.IsConnected) continue;
            try { await discovery.RunAndReportAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Hardware scan failed");
            }
        }
    }
}
