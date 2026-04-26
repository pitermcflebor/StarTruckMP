using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Core.Services;

public sealed class OverlayModeService
{
    private readonly object _syncRoot = new();
    private readonly IOverlayLogger _logger;
    private bool _uiModeEnabled;

    public OverlayModeService(IOverlayLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<bool>? UiModeChanged;

    public bool UiModeEnabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _uiModeEnabled;
            }
        }
    }

    public bool EnableUiMode() => SetUiMode(true, "EnableUiMode");

    public bool DisableUiMode() => SetUiMode(false, "DisableUiMode");

    public bool ToggleUiMode()
    {
        lock (_syncRoot)
        {
            return SetUiModeCore(!_uiModeEnabled, "ToggleUiMode");
        }
    }

    private bool SetUiMode(bool enabled, string source)
    {
        lock (_syncRoot)
        {
            return SetUiModeCore(enabled, source);
        }
    }

    private bool SetUiModeCore(bool enabled, string source)
    {
        if (_uiModeEnabled == enabled)
        {
            _logger.Info($"[Mode] {source}: uiMode was already {(enabled ? "ON" : "OFF")}");
            return false;
        }

        _uiModeEnabled = enabled;
        _logger.Info($"[Mode] {source}: uiMode => {(enabled ? "ON" : "OFF")}");
        UiModeChanged?.Invoke(this, enabled);
        return true;
    }
}

