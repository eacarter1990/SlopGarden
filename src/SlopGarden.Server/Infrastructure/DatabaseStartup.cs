using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

/// <summary>Applies migrations and seed data once the host is starting, including when running as a Windows service.</summary>
public sealed class DatabaseStartup(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
