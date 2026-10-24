using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SlopGarden.Emulator.Simulation;

public sealed class EmulatorOptions
{
    /// <summary>Optional JSON scenario file. Loaded at startup if it exists and rewritten when definitions change.</summary>
    public string? ScenarioFile { get; set; }
    public int TickMilliseconds { get; set; } = 200;
}

public static class ScenarioFile
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ScenarioDocument? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ScenarioDocument>(File.ReadAllText(path), JsonOptions);
    }

    public static void Save(string path, ScenarioDocument scenario)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(scenario, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Advances the simulation in real time and persists definition changes to the scenario file.</summary>
public sealed class SimulationLoop(GardenSimulator simulator, IOptions<EmulatorOptions> options, ILogger<SimulationLoop> logger) : BackgroundService
{
    private volatile bool _dirty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        simulator.DefinitionsChanged += MarkDirty;
        try
        {
            var tick = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.TickMilliseconds, 20, 5000));
            using var timer = new PeriodicTimer(tick);
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = clock.Elapsed;
                simulator.Advance(now - last);
                last = now;
                if (_dirty) Persist();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            simulator.DefinitionsChanged -= MarkDirty;
            if (_dirty) Persist();
        }
    }

    private void MarkDirty() => _dirty = true;

    private void Persist()
    {
        _dirty = false;
        var path = options.Value.ScenarioFile;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            ScenarioFile.Save(path, simulator.Export());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save scenario file {ScenarioFile}", path);
        }
    }
}
