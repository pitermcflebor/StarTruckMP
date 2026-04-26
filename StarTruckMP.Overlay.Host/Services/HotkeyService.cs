using Avalonia.Input;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Core.Services;
using StarTruckMP.Overlay.Host.UI;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class HotkeyService : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly OverlayModeService _modeService;
    private readonly IOverlayLogger _logger;
    private bool _disposed;

    public HotkeyService(OverlayWindow window, OverlayModeService modeService, IOverlayLogger logger)
    {
        _window = window;
        _modeService = modeService;
        _logger = logger;
        _window.KeyDown += OnKeyDown;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.KeyDown -= OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            _logger.Info("[Hotkey] F2 => toggle UI mode");
            _modeService.ToggleUiMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _modeService.UiModeEnabled)
        {
            _logger.Info("[Hotkey] Esc => disable UI mode");
            _modeService.DisableUiMode();
            e.Handled = true;
        }
    }
}

