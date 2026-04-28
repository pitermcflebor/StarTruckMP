using System.Runtime.InteropServices;
using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Host.Interop;

internal sealed class Win32WindowStyles
{
    private readonly IOverlayLogger _logger;

    public Win32WindowStyles(IOverlayLogger logger)
    {
        _logger = logger;
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsPopup = unchecked((int)0x80000000);
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimize = 0x20000000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsExTransparent = 0x20L;
    private const long WsExLayered = 0x80000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmncrpDisabled = 1;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFEu);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwShownoactivate = 4;
    private const int SwShow = 5;

    public void ApplyOverlayWindowStyle(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickFrame | WsMinimize | WsMaximizeBox | WsSysMenu);
        style |= WsPopup;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        TrySuppressWindowFrame(hwnd);
        _logger.Info($"[Win32] Applied overlay base style to hwnd=0x{hwnd:X}");
    }

    public void SetClickThrough(nint hwnd, bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExLayered | WsExToolWindow;
        style = enabled
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        var flags = SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged;
        if (enabled)
            flags |= SwpNoActivate;

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
        TrySuppressWindowFrame(hwnd);
        _logger.Info($"[Win32] Click-through => {(enabled ? "ON" : "OFF")} hwnd=0x{hwnd:X}, noActivate => {(enabled ? "ON" : "OFF")}");
    }

    public void FocusWindow(nint hwnd)
    {
        ClipCursor(IntPtr.Zero);
        ShowWindow(hwnd, SwShow);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        SetFocus(hwnd);
        _logger.Info($"[Win32] FocusWindow hwnd=0x{hwnd:X}");
    }

    public void FocusGameWindow(nint hwnd)
    {
        if (hwnd == 0)
            return;

        ShowWindow(hwnd, SwShow);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        SetActiveWindow(hwnd);
        SetFocus(hwnd);
        _logger.Info($"[Win32] FocusGameWindow hwnd=0x{hwnd:X}");
    }

    public void ShowWithoutActivation(nint hwnd)
    {
        ShowWindow(hwnd, SwShownoactivate);
        _logger.Info($"[Win32] ShowWithoutActivation hwnd=0x{hwnd:X}");
    }

    private void TrySuppressWindowFrame(nint hwnd)
    {
        TrySetDwmWindowAttribute(hwnd, DwmwaNcRenderingPolicy, DwmncrpDisabled);
        TrySetDwmWindowAttribute(hwnd, DwmwaWindowCornerPreference, DwmwcpDoNotRound);
        TrySetDwmWindowAttribute(hwnd, DwmwaBorderColor, DwmColorNone);
    }

    private static void TrySetDwmWindowAttribute(nint hwnd, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Ignored: some DWM attributes are not available on every Windows version.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(nint hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}

