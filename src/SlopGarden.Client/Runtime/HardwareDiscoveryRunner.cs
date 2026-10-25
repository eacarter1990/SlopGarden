using SlopGarden.Client.Plugins;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Runtime;

/// <summary>
/// Asks every loaded <see cref="SlopGarden.Plugins.Abstractions.IHardwareDiscovery"/> what is attached.
/// A result is kept only when its plugin id is a sensor, actuator, or camera the client can actually run.
/// The same set is not sent again until it changes, unless the server asks.
/// </summary>
public sealed class HardwareDiscoveryRunner(PluginRegistry plugins, IServiceProvider services, IServerLink link, ILogger<HardwareDiscoveryRunner> logger)
{
    private static readonly TimeSpan PerPluginTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _reported;

    public async Task<IReadOnlyList<DiscoveredHardwareItem>> DiscoverAsync(CancellationToken ct)
        => (await ScanAsync(ct)).Items;

    public async Task RunAndReportAsync(CancellationToken ct, bool force = false)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var scan = await ScanAsync(ct);
            var signature = Signature(scan);
            if (!force && signature == _reported) return;
            logger.LogInformation("Hardware discovery found {Count} item(s){Note}", scan.Items.Count, scan.Complete ? "" : "; a plugin failed, so missing hardware is kept");
            if (!link.IsConnected) return;
            await link.InvokeAsync(DeviceHubMethods.ReportDiscoveredHardware, new DiscoveredHardwareReport(scan.Items, scan.Complete), ct);
            _reported = signature;
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

    private async Task<Scan> ScanAsync(CancellationToken ct)
    {
        var tasks = plugins.Discoveries.Select(async d =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerPluginTimeout);
            try
            {
                var found = await d.DiscoverAsync(services, timeout.Token);
                List<DiscoveredHardwareItem> items = found.Select(x => new DiscoveredHardwareItem(x.PluginId, x.Kind, x.Address, x.SuggestedName, x.SuggestedSettings,
                    x.Channels.Select(c => new DiscoveredChannel(c.Key, c.Metric, c.Unit)).ToList(), x.ActuatorKind)).ToList();
                return (Ok: true, Items: items);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Hardware discovery {Discovery} failed", d.PluginId);
                return (Ok: false, Items: new List<DiscoveredHardwareItem>());
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var items = results.SelectMany(r => r.Items).Where(Known).ToList();
        return new Scan(items, results.All(r => r.Ok));
    }

    private bool Known(DiscoveredHardwareItem item)
    {
        if (plugins.FindSensor(item.PluginId) is not null || plugins.FindActuator(item.PluginId) is not null || plugins.FindCamera(item.PluginId) is not null)
            return true;
        logger.LogDebug("Ignoring discovered {Address}: no loaded plugin {PluginId}", item.Address, item.PluginId);
        return false;
    }

    private static string Signature(Scan scan)
    {
        var rows = scan.Items.OrderBy(i => i.PluginId, StringComparer.Ordinal).ThenBy(i => i.Address, StringComparer.Ordinal)
            .Select(i => $"{i.PluginId}|{i.Address}|{i.SuggestedName}");
        return (scan.Complete ? "1" : "0") + "\n" + string.Join("\n", rows);
    }

    private sealed record Scan(IReadOnlyList<DiscoveredHardwareItem> Items, bool Complete);
}
