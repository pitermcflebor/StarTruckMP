using System.IO.Pipes;
using System.Text;
using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class GamePipeClient
{
    private const string GamePipeName = "StarTruckMP.Client";
    private readonly IOverlayLogger _logger;

    public GamePipeClient(IOverlayLogger logger)
    {
        _logger = logger;
    }

    public void SendJsonLine(string json)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", GamePipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Pipe→Game] Failed to send message: {ex.Message}");
        }
    }
}

