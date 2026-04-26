using System.IO.Pipes;
using System.Text;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Core.Ipc;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class OverlayPipeServer : IDisposable
{
    private const string OverlayPipeName = "StarTruckMP.Overlay";
    private readonly IOverlayLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<OverlayCommand, Task> _commandHandler;
    private Task? _serverTask;

    public OverlayPipeServer(IOverlayLogger logger, Func<OverlayCommand, Task> commandHandler)
    {
        _logger = logger;
        _commandHandler = commandHandler;
    }

    public void Start()
    {
        if (_serverTask != null)
            return;

        _logger.Info($"[Pipe<-Game] Starting server '{OverlayPipeName}'");
        _serverTask = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    OverlayPipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);

                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    var command = OverlayCommandParser.Parse(line);
                    await _commandHandler(command).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Warn($"[Pipe<-Game] Pipe server error: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }
        _cts.Dispose();
    }
}

