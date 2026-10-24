using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Scheduling;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Tests.Scheduling;

public sealed class CronScheduleTests
{
    private static ScheduleConfig Schedule(string cron, string tz, Guid? actuatorId = null, bool enabled = true, int duration = 30) =>
        new(Guid.NewGuid(), "Water", actuatorId ?? Guid.NewGuid(), cron, tz, duration, null, enabled);

    [Fact]
    public void Next_run_is_evaluated_in_the_schedule_time_zone()
    {
        // 06:00 in Berlin during winter (UTC+1) is 05:00 UTC.
        var next = CronSchedule.Next(Schedule("0 6 * * *", "Europe/Berlin"), new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 5, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_run_follows_daylight_saving_changes()
    {
        // DST starts in Europe on 2026-03-29: 06:00 local moves from 05:00 UTC to 04:00 UTC.
        var schedule = Schedule("0 6 * * *", "Europe/Berlin");
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 5, 0, 0, TimeSpan.Zero), CronSchedule.Next(schedule, new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.Zero), CronSchedule.Next(schedule, new DateTimeOffset(2026, 3, 28, 6, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Next_run_is_strictly_after_the_given_instant()
    {
        var schedule = Schedule("*/15 * * * *", "UTC");
        var at = new DateTimeOffset(2026, 5, 1, 10, 15, 0, TimeSpan.Zero);
        Assert.Equal(at.AddMinutes(15), CronSchedule.Next(schedule, at));
    }

    [Fact]
    public void Weekday_schedules_resolve_in_local_time()
    {
        // Mondays 07:30 in New York (EDT, UTC-4). 2026-06-08 is a Monday.
        var next = CronSchedule.Next(Schedule("30 7 * * 1", "America/New_York"), new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 11, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Unknown_time_zone_falls_back_to_utc()
    {
        var next = CronSchedule.Next(Schedule("0 6 * * *", "Mars/Olympus_Mons"), new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero), next);
    }

    private static (LocalScheduler Scheduler, FakeTimeProvider Time, ActuatorController Controller, FakeActuatorPlugin Plugin, RecordingReporter Reporter) CreateScheduler(DateTimeOffset now)
    {
        var time = new FakeTimeProvider(now);
        var ledger = new InMemoryRunLedger();
        var values = new ChannelValueStore();
        var reporter = new RecordingReporter();
        var plugin = new FakeActuatorPlugin();
        var registry = new PluginRegistry([], [], NullLogger<PluginRegistry>.Instance);
        registry.RegisterBuiltin(plugin);
        var controller = new ActuatorController(registry, new SafetyGuard(ledger, values, time), ledger, values, reporter, Configs.EmptyServices, time, Configs.NullLogs);
        return (new LocalScheduler(controller, time, NullLogger<LocalScheduler>.Instance), time, controller, plugin, reporter);
    }

    [Fact]
    public void Invalid_cron_is_reported_as_a_config_error()
    {
        var (scheduler, _, _, _, _) = CreateScheduler(DateTimeOffset.UtcNow);
        var errors = new List<string>();
        scheduler.Update([Schedule("every morning", "UTC"), Schedule("0 6 * * *", "UTC")], errors);
        Assert.Single(errors);
        Assert.Contains("invalid cron", errors[0]);
        Assert.Single(scheduler.Entries);
    }

    [Fact]
    public void Disabled_schedules_are_ignored()
    {
        var (scheduler, _, _, _, _) = CreateScheduler(DateTimeOffset.UtcNow);
        scheduler.Update([Schedule("0 6 * * *", "UTC", enabled: false)], []);
        Assert.Empty(scheduler.Entries);
    }

    [Fact]
    public async Task Scheduler_runs_the_actuator_when_due_without_a_server()
    {
        var start = new DateTimeOffset(2026, 6, 10, 5, 59, 0, TimeSpan.Zero);
        var (scheduler, time, controller, plugin, reporter) = CreateScheduler(start);
        var actuator = Configs.Actuator();
        await controller.ApplyAsync(Configs.Document(actuators: [actuator]), []);
        var schedule = Schedule("0 6 * * *", "UTC", actuator.Id, duration: 20);
        scheduler.Update([schedule], []);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 6, 0, 0, TimeSpan.Zero), scheduler.Entries.Single().Next);

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        try
        {
            var device = plugin.Devices[actuator.Id];
            for (var i = 0; i < 400 && !device.On; i++)
            {
                await Task.Delay(5);
                time.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.True(device.On);
            for (var i = 0; i < 400 && device.On; i++)
            {
                await Task.Delay(5);
                time.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.False(device.On);
            await Wait.UntilAsync(() => reporter.Runs.Count == 1, TimeSpan.FromSeconds(5), "the scheduled run is recorded");
            var run = reporter.Runs.Single();
            Assert.Equal(schedule.Id, run.ScheduleId);
            Assert.Equal(RunSources.Schedule, run.Source);
            Assert.Equal(new DateTimeOffset(2026, 6, 11, 6, 0, 0, TimeSpan.Zero), scheduler.Entries.Single().Next);
        }
        finally
        {
            await cts.CancelAsync();
            await scheduler.StopAsync(CancellationToken.None);
            await controller.DisposeAsync();
        }
    }
}
