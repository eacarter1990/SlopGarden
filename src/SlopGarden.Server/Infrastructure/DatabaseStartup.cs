using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

/// <summary>Applies migrations and seed data once the host is starting, including when running as a Windows service.</summary>
public sealed class DatabaseStartup(IServiceScopeFactory scopes, ILogger<DatabaseStartup> log) : IHostedService
{
    public static string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync(cancellationToken);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            log.LogError(ex, "PostgreSQL is not reachable. The site will stay up on port 5080 so the database can be pointed at a running server.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
