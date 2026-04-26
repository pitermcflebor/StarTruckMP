namespace StarTruckMP.Overlay.Core.Abstractions;

public interface IOverlayLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}

