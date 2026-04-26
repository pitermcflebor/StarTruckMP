using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Core.Models;

namespace StarTruckMP.Overlay.Host.Interop;

internal sealed class GameWindowTracker : IDisposable
{
    private readonly IOverlayLogger _logger;
    private readonly nint _gameWindowHandle;
    private readonly int _gameProcessId;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private GameWindowState _lastState;

    public GameWindowTracker(nint gameWindowHandle, int gameProcessId, IOverlayLogger logger)
    {
        _gameWindowHandle = gameWindowHandle;
        _gameProcessId = gameProcessId;
        _logger = logger;
        _lastState = GameWindowState.Empty(gameWindowHandle);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler<GameWindowState>? StateChanged;

    public event EventHandler? GameExited;

    public GameWindowState CurrentState => _lastState;

    public void Start()
    {
        _logger.Info($"[Tracker] Start hwnd=0x{_gameWindowHandle:X}, pid={_gameProcessId}");
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        if (_disposed)
            return;

        if (_gameProcessId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(_gameProcessId);
                if (process.HasExited)
                {
                    _logger.Warn("[Tracker] The game process has exited.");
                    GameExited?.Invoke(this, EventArgs.Empty);
                    Stop();
                    return;
                }
            }
            catch (ArgumentException)
            {
                _logger.Warn("[Tracker] The game process was not found; closing the overlay.");
                GameExited?.Invoke(this, EventArgs.Empty);
                Stop();
                return;
            }
        }

        if (_gameWindowHandle == 0 || !IsWindow(_gameWindowHandle))
        {
            _logger.Warn("[Tracker] The game HWND is no longer valid.");
            GameExited?.Invoke(this, EventArgs.Empty);
            Stop();
            return;
        }

        if (!TryGetTrackedWindowRect(_gameWindowHandle, out var rect))
            return;

        var pixelWidth = Math.Max(1, rect.Right - rect.Left);
        var pixelHeight = Math.Max(1, rect.Bottom - rect.Top);
        var dpi = GetDpiForWindow(_gameWindowHandle);
        var dpiScale = dpi <= 0 ? 1d : dpi / 96d;

        var state = new GameWindowState(
            _gameWindowHandle,
            rect.Left,
            rect.Top,
            pixelWidth,
            pixelHeight,
            dpiScale,
            dpiScale,
            IsWindowVisible(_gameWindowHandle),
            IsIconic(_gameWindowHandle));

        if (state != _lastState)
        {
            _lastState = state;
            StateChanged?.Invoke(this, state);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
    }

    private bool TryGetTrackedWindowRect(nint hwnd, out Rect rect)
    {
        if (GetClientRect(hwnd, out var clientRect))
        {
            var topLeft = new Point { X = clientRect.Left, Y = clientRect.Top };
            var bottomRight = new Point { X = clientRect.Right, Y = clientRect.Bottom };

            if (ClientToScreen(hwnd, ref topLeft) && ClientToScreen(hwnd, ref bottomRight))
            {
                rect = new Rect
                {
                    Left = topLeft.X,
                    Top = topLeft.Y,
                    Right = bottomRight.X,
                    Bottom = bottomRight.Y
                };

                if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                    return true;
            }
        }

        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rect>()) == 0)
            return true;

        return GetWindowRect(hwnd, out rect);
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}

