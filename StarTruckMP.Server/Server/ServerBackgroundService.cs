using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StarTruckMP.Server.Server.Services;

namespace StarTruckMP.Server.Server;

public class ServerBackgroundService(ServerManager serverManager) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        serverManager.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                serverManager.Polling();
                await Task.Delay(15, cancellationToken);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        serverManager.Stop();
        return Task.CompletedTask;
    }
}