using Avalonia;
using Avalonia.Input;
using CefSharp;
using StarTruckMP.Overlay.Browser;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Core.Services;
using StarTruckMP.Overlay.Host.UI;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class BrowserInputRouter : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly CefOverlayBrowser _browser;
    private readonly OverlayModeService _modeService;
    private bool _disposed;

    public BrowserInputRouter(
        OverlayWindow window,
        CefOverlayBrowser browser,
        OverlayModeService modeService,
        IOverlayLogger logger)
    {
        _window = window;
        _browser = browser;
        _modeService = modeService;
        _window.PointerMoved += OnPointerMoved;
        _window.PointerPressed += OnPointerPressed;
        _window.PointerReleased += OnPointerReleased;
        _window.PointerWheelChanged += OnPointerWheelChanged;
        _window.KeyDown += OnKeyDown;
        _window.KeyUp += OnKeyUp;
        _window.TextInput += OnTextInput;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.PointerMoved -= OnPointerMoved;
        _window.PointerPressed -= OnPointerPressed;
        _window.PointerReleased -= OnPointerReleased;
        _window.PointerWheelChanged -= OnPointerWheelChanged;
        _window.KeyDown -= OnKeyDown;
        _window.KeyUp -= OnKeyUp;
        _window.TextInput -= OnTextInput;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanRoutePointer())
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var point = ToBrowserPoint(e.GetPosition(_window));
        host.SendMouseMoveEvent(new MouseEvent(point.X, point.Y, GetMouseModifiers(e.KeyModifiers)), mouseLeave: false);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanRoutePointer())
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var point = ToBrowserPoint(e.GetPosition(_window));
        var properties = e.GetCurrentPoint(_window).Properties;
        var button = ToMouseButton(properties);
        host.SendMouseClickEvent(new MouseEvent(point.X, point.Y, GetMouseModifiers(e.KeyModifiers)), button, mouseUp: false, clickCount: 1);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!CanRoutePointer())
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var point = ToBrowserPoint(e.GetPosition(_window));
        var properties = e.GetCurrentPoint(_window).Properties;
        var button = ToMouseButton(properties);
        host.SendMouseClickEvent(new MouseEvent(point.X, point.Y, GetMouseModifiers(e.KeyModifiers)), button, mouseUp: true, clickCount: 1);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!CanRoutePointer())
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var point = ToBrowserPoint(e.GetPosition(_window));
        var deltaX = (int)Math.Round(e.Delta.X * 120d);
        var deltaY = (int)Math.Round(e.Delta.Y * 120d);
        host.SendMouseWheelEvent(new MouseEvent(point.X, point.Y, GetMouseModifiers(e.KeyModifiers)), deltaX, deltaY);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_modeService.UiModeEnabled)
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var virtualKey = ToVirtualKey(e.Key);
        if (virtualKey == 0)
            return;

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.RawKeyDown,
            WindowsKeyCode = virtualKey,
            NativeKeyCode = virtualKey,
            Modifiers = GetKeyModifiers(e.KeyModifiers),
            IsSystemKey = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
        });

        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_modeService.UiModeEnabled)
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        var virtualKey = ToVirtualKey(e.Key);
        if (virtualKey == 0)
            return;

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.KeyUp,
            WindowsKeyCode = virtualKey,
            NativeKeyCode = virtualKey,
            Modifiers = GetKeyModifiers(e.KeyModifiers),
            IsSystemKey = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
        });

        e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!_modeService.UiModeEnabled || string.IsNullOrEmpty(e.Text))
            return;

        var host = _browser.BrowserHost;
        if (host == null)
            return;

        foreach (var ch in e.Text)
        {
            host.SendKeyEvent(new KeyEvent
            {
                Type = KeyEventType.Char,
                WindowsKeyCode = ch,
                NativeKeyCode = ch
            });
        }

        e.Handled = true;
    }

    private bool CanRoutePointer() => _modeService.UiModeEnabled && _browser.BrowserHost != null;

    private PixelPoint ToBrowserPoint(Point point)
    {
        var clamped = _window.ClampToClient(point);
        var width = Math.Max(1d, _window.Bounds.Width);
        var height = Math.Max(1d, _window.Bounds.Height);
        var x = (int)Math.Round(clamped.X * _browser.PixelWidth / width);
        var y = (int)Math.Round(clamped.Y * _browser.PixelHeight / height);
        return new PixelPoint(Math.Clamp(x, 0, Math.Max(0, _browser.PixelWidth - 1)), Math.Clamp(y, 0, Math.Max(0, _browser.PixelHeight - 1)));
    }

    private static CefEventFlags GetMouseModifiers(KeyModifiers modifiers)
    {
        var flags = CefEventFlags.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) flags |= CefEventFlags.ShiftDown;
        if (modifiers.HasFlag(KeyModifiers.Control)) flags |= CefEventFlags.ControlDown;
        if (modifiers.HasFlag(KeyModifiers.Alt)) flags |= CefEventFlags.AltDown;
        return flags;
    }

    private static CefEventFlags GetKeyModifiers(KeyModifiers modifiers)
    {
        var flags = GetMouseModifiers(modifiers);
        if (modifiers.HasFlag(KeyModifiers.Meta)) flags |= CefEventFlags.CommandDown;
        return flags;
    }

    private static MouseButtonType ToMouseButton(PointerPointProperties properties)
    {
        if (properties.IsMiddleButtonPressed)
            return MouseButtonType.Middle;

        if (properties.IsRightButtonPressed)
            return MouseButtonType.Right;

        return MouseButtonType.Left;
    }

    private static int ToVirtualKey(Key key)
    {
        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.D0 => 0x30,
            Key.D1 => 0x31,
            Key.D2 => 0x32,
            Key.D3 => 0x33,
            Key.D4 => 0x34,
            Key.D5 => 0x35,
            Key.D6 => 0x36,
            Key.D7 => 0x37,
            Key.D8 => 0x38,
            Key.D9 => 0x39,
            Key.A => 0x41,
            Key.B => 0x42,
            Key.C => 0x43,
            Key.D => 0x44,
            Key.E => 0x45,
            Key.F => 0x46,
            Key.G => 0x47,
            Key.H => 0x48,
            Key.I => 0x49,
            Key.J => 0x4A,
            Key.K => 0x4B,
            Key.L => 0x4C,
            Key.M => 0x4D,
            Key.N => 0x4E,
            Key.O => 0x4F,
            Key.P => 0x50,
            Key.Q => 0x51,
            Key.R => 0x52,
            Key.S => 0x53,
            Key.T => 0x54,
            Key.U => 0x55,
            Key.V => 0x56,
            Key.W => 0x57,
            Key.X => 0x58,
            Key.Y => 0x59,
            Key.Z => 0x5A,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,
            Key.NumPad0 => 0x60,
            Key.NumPad1 => 0x61,
            Key.NumPad2 => 0x62,
            Key.NumPad3 => 0x63,
            Key.NumPad4 => 0x64,
            Key.NumPad5 => 0x65,
            Key.NumPad6 => 0x66,
            Key.NumPad7 => 0x67,
            Key.NumPad8 => 0x68,
            Key.NumPad9 => 0x69,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.F1 => 0x70,
            Key.F2 => 0x71,
            Key.F3 => 0x72,
            Key.F4 => 0x73,
            Key.F5 => 0x74,
            Key.F6 => 0x75,
            Key.F7 => 0x76,
            Key.F8 => 0x77,
            Key.F9 => 0x78,
            Key.F10 => 0x79,
            Key.F11 => 0x7A,
            Key.F12 => 0x7B,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => 0
        };
    }
}


