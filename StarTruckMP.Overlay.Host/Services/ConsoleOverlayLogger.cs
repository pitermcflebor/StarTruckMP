using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class ConsoleOverlayLogger : IOverlayLogger
{
    private static readonly object SyncRoot = new();

    public void Info(string message) => Write("INF", message);

    public void Warn(string message) => Write("WRN", message);

    public void Error(string message) => Write("ERR", message);

    public void Error(string message, Exception exception) => Write("ERR", $"{message} {exception}");

    private static void Write(string level, string message)
    {
        lock (SyncRoot)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
        }
    }
}

