namespace StarTruckMP.Overlay.Core.Models;

public sealed record GameWindowState(
    nint Handle,
    int Left,
    int Top,
    int PixelWidth,
    int PixelHeight,
    double DpiScaleX,
    double DpiScaleY,
    bool IsVisible,
    bool IsMinimized)
{
    public static GameWindowState Empty(nint handle) => new(
        handle,
        0,
        0,
        1280,
        720,
        1d,
        1d,
        true,
        false);

    public double DipWidth => PixelWidth / (DpiScaleX <= 0d ? 1d : DpiScaleX);

    public double DipHeight => PixelHeight / (DpiScaleY <= 0d ? 1d : DpiScaleY);
}

