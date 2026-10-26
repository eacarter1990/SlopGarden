using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

/// <summary>
/// Applies migrations and seed data after the process has answered the service manager.
/// A Windows service often starts before local PostgreSQL is listening. The first attempt is awaited
/// so a database that is already up is ready before the host finishes starting. A failure returns
/// immediately and retries in the background, leaving the site up on port 5080.
/// </summary>
public sealed class DatabaseStartup(IServiceScopeFactory scopes, ILogger<DatabaseStartup> log) : IHostedService
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _retry;
    private int _attempts;

    public bool Ready { get; private set; }
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await TryPrepareAsync(cancellationToken)) return;
        ScheduleRetry();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        Task? retry;
        lock (_gate) retry = _retry;
        if (retry is not null)
            await retry.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>A later request found the database gone. Keep trying so the setup page can leave the waiting state.</summary>
    public void MarkUnavailable(Exception ex)
    {
        LastError = Describe(ex);
        Ready = false;
        log.LogWarning(ex, "PostgreSQL stopped answering. {Detail}", LastError);
        ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_retry is { IsCompleted: false }) return;
            _retry = Task.Run(() => RetryAsync(_stop.Token));
        }
    }

    private async Task RetryAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            if (await TryPrepareAsync(ct)) return;
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    private async Task<bool> TryPrepareAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync(ct);
            LastError = null;
            Ready = true;
            if (_attempts > 0)
                log.LogInformation("PostgreSQL is reachable. The admin setup can continue.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = Describe(ex);
            Ready = false;
            if (_attempts == 0)
                log.LogError(ex, "PostgreSQL is not reachable. The site stays up and will keep trying. {Detail}", LastError);
            else
                log.LogWarning("Still waiting for PostgreSQL (attempt {Attempt}). {Detail}", _attempts + 1, LastError);
            _attempts++;
            return false;
        }
    }

    public static string Describe(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return "PostgreSQL is not running on 127.0.0.1:5432. Start the PostgreSQL service, then create database slopgarden owned by user slopgarden with password slopgarden.";
            if (e is PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName })
                return "Database slopgarden does not exist. Create a login role slopgarden with password slopgarden and a database slopgarden owned by that role. This page opens the admin setup once that database answers.";
            if (e is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification })
                return "PostgreSQL rejected the slopgarden login. Create the role with password slopgarden, or set the database password in the server settings file and restart the SlopGarden Server service.";
        }
        return ex.GetBaseException().Message;
    }
}
