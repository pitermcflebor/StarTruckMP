using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace StarTruckMP.Overlay.Host.UI;

internal sealed class OverlayWindow : Window
{
    private readonly Image _image;

    public OverlayWindow()
    {
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        IsHitTestVisible = true;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        Focusable = true;

        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children =
            {
                _image
            }
        };
    }

    public Bitmap? FrameBitmap
    {
        get => _image.Source as Bitmap;
        set => _image.Source = value;
    }

    public void NotifyFrameUpdated()
    {
        _image.InvalidateVisual();
        (Content as Control)?.InvalidateVisual();
        InvalidateVisual();
    }

    public nint GetWindowHandle()
    {
        var handle = TryGetPlatformHandle();
        return handle == null ? 0 : handle.Handle;
    }

    public void UpdateBounds(int left, int top, double dipWidth, double dipHeight)
    {
        Position = new PixelPoint(left, top);
        Width = Math.Max(1d, dipWidth);
        Height = Math.Max(1d, dipHeight);
    }

    public Point ClampToClient(Point point)
    {
        var x = Math.Clamp(point.X, 0, Bounds.Width <= 0 ? 0 : Bounds.Width - 1);
        var y = Math.Clamp(point.Y, 0, Bounds.Height <= 0 ? 0 : Bounds.Height - 1);
        return new Point(x, y);
    }

    protected override Type StyleKeyOverride => typeof(Window);
}

