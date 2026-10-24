using SlopGarden.Client.Plugins;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Runtime;

/// <summary>Runs every plugin's <c>IHardwareDiscovery</c> and reports the combined result to the server.</summary>
public sealed class HardwareDiscoveryRunner(PluginRegistry plugins, IServiceProvider services, IServerLink link, ILogger<HardwareDiscoveryRunner> logger)
{
    private static readonly TimeSpan PerPluginTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DiscoveredHardwareItem>> DiscoverAsync(CancellationToken ct)
    {
        var tasks = plugins.Discoveries.Select(async d =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerPluginTimeout);
            try
            {
                var found = await d.DiscoverAsync(services, timeout.Token);
                return found.Select(x => new DiscoveredHardwareItem(x.PluginId, x.Kind, x.Address, x.SuggestedName, x.SuggestedSettings,
                    x.Channels.Select(c => new DiscoveredChannel(c.Key, c.Metric, c.Unit)).ToList(), x.ActuatorKind)).ToList();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Hardware discovery {Discovery} failed", d.PluginId);
                return [];
            }
        });
        return (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
    }

    public async Task RunAndReportAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var items = await DiscoverAsync(ct);
            logger.LogInformation("Hardware discovery found {Count} item(s)", items.Count);
            if (link.IsConnected) await link.InvokeAsync(DeviceHubMethods.ReportDiscoveredHardware, new DiscoveredHardwareReport(items), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reporting discovered hardware failed");
        }
        finally
        {
            _gate.Release();
        }
    }
}
