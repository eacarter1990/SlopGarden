namespace SlopGarden.Client.Actuators;

/// <summary>Runs <see cref="ActuatorController.SuperviseAsync"/> about once a second (flow watchdogs, continuous runs),
/// also while offline.</summary>
public sealed class ActuatorSupervisor(ActuatorController actuators, TimeProvider time, ILogger<ActuatorSupervisor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await actuators.SuperviseAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Actuator supervision failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
