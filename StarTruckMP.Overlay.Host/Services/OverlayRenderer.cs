using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using StarTruckMP.Overlay.Browser;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Host.UI;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class OverlayRenderer
{
    private readonly OverlayWindow _window;
    private readonly IOverlayLogger _logger;
    private WriteableBitmap? _bitmap;
    private int _lastWidth;
    private int _lastHeight;
    public OverlayRenderer(OverlayWindow window, IOverlayLogger logger)
    {
        _window = window;
        _logger = logger;
    }

    public void Render(BrowserFrameReadyEventArgs frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        if (_bitmap == null || frame.Width != _lastWidth || frame.Height != _lastHeight)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            _lastWidth = frame.Width;
            _lastHeight = frame.Height;
            _window.FrameBitmap = _bitmap;
            _logger.Info($"[Renderer] Created surface {frame.Width}x{frame.Height}");
        }

        using var locked = _bitmap.Lock();
        Marshal.Copy(frame.Buffer, 0, locked.Address, Math.Min(frame.Buffer.Length, frame.Height * frame.Stride));
        _window.FrameBitmap = _bitmap;
        _window.NotifyFrameUpdated();

    }
}

