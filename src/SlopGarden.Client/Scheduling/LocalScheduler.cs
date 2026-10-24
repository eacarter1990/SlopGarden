using Cronos;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Scheduling;

public static class CronSchedule
{
    public static CronExpression Parse(string cron) => CronExpression.Parse(cron.Trim(), CronFormat.Standard);

    /// <summary>Next occurrence strictly after <paramref name="afterUtc"/>, evaluated in the schedule's time zone.</summary>
    public static DateTimeOffset? Next(ScheduleConfig schedule, DateTimeOffset afterUtc) =>
        Parse(schedule.Cron).GetNextOccurrence(afterUtc, TimeZones.Find(schedule.TimeZoneId));
}

/// <summary>Runs approved schedules locally, so watering continues while the server is unreachable.</summary>
public sealed class LocalScheduler(ActuatorController actuators, TimeProvider time, ILogger<LocalScheduler> logger) : BackgroundService
{
    /// <summary>Occurrences missed by more than this (host suspended, clock jump) are skipped instead of run late.</summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly AsyncSignal _changed = new();
    private Dictionary<Guid, (ScheduleConfig Schedule, DateTimeOffset? Next)> _entries = [];

    public void Update(IReadOnlyList<ScheduleConfig> schedules, List<string> errors)
    {
        var now = time.GetUtcNow();
        var entries = new Dictionary<Guid, (ScheduleConfig, DateTimeOffset?)>();
        foreach (var s in schedules.Where(s => s.Enabled))
        {
            try
            {
                entries[s.Id] = (s, CronSchedule.Next(s, now));
            }
            catch (CronFormatException ex)
            {
                errors.Add($"Schedule '{s.Name}' ({s.Id}): invalid cron '{s.Cron}': {ex.Message}");
            }
        }
        lock (_gate) _entries = entries;
        _changed.Set();
    }

    public IReadOnlyList<(ScheduleConfig Schedule, DateTimeOffset? Next)> Entries
    {
        get { lock (_gate) return _entries.Values.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var due = new List<ScheduleConfig>();
            DateTimeOffset? earliest = null;
            lock (_gate)
            {
                foreach (var (id, (schedule, next)) in _entries.ToList())
                {
                    if (next is null) continue;
                    if (next <= now)
                    {
                        if (now - next.Value <= MaxLateness) due.Add(schedule);
                        else logger.LogWarning("Schedule {ScheduleName} missed its {Occurrence} run; skipping", schedule.Name, next);
                        var following = CronSchedule.Next(schedule, now);
                        _entries[id] = (schedule, following);
                        if (following is { } f && (earliest is null || f < earliest)) earliest = f;
                    }
                    else if (earliest is null || next < earliest)
                    {
                        earliest = next;
                    }
                }
            }
            foreach (var schedule in due)
            {
                logger.LogInformation("Running schedule {ScheduleName}", schedule.Name);
                _ = RunAsync(schedule);
            }
            var wait = earliest is { } e ? e - time.GetUtcNow() : TimeSpan.FromMinutes(1);
            wait = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 10, 60_000));
            try
            {
                await WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(wait, time, cts.Token);
        var signal = _changed.WaitAsync(Timeout.InfiniteTimeSpan, cts.Token);
        await Task.WhenAny(delay, signal);
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(ScheduleConfig schedule)
    {
        try
        {
            await actuators.RunScheduleAsync(schedule);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schedule {ScheduleName} failed", schedule.Name);
        }
    }
}
