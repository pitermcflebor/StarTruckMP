using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StarTruckMP.Server.Server.Services;

namespace StarTruckMP.Server.Server;

public class ServerBackgroundService(ServerManager serverManager) : IHostedService
{
    private Task? _pollingTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        serverManager.Start();

        // Run the polling loop in a separate Task so StartAsync returns immediately,
        // allowing Kestrel and the rest of the host to finish starting up.
        _pollingTask = Task.Run(async () =>
        {
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
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        serverManager.Stop();
        if (_pollingTask is not null)
            await _pollingTask.ConfigureAwait(false);
    }
}