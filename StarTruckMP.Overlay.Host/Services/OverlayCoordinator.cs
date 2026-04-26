using Avalonia.Threading;
using StarTruckMP.Overlay.Browser;
using StarTruckMP.Overlay.Core.Abstractions;
using StarTruckMP.Overlay.Core.Ipc;
using StarTruckMP.Overlay.Core.Models;
using StarTruckMP.Overlay.Core.Services;
using StarTruckMP.Overlay.Host.Interop;
using StarTruckMP.Overlay.Host.UI;

namespace StarTruckMP.Overlay.Host.Services;

internal sealed class OverlayCoordinator : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly CefOverlayBrowser _browser;
    private readonly OverlayRenderer _renderer;
    private readonly OverlayModeService _modeService;
    private readonly GameWindowTracker _tracker;
    private readonly Win32WindowStyles _windowStyles;
    private readonly OverlayPipeServer _pipeServer;
    private readonly IOverlayLogger _logger;
    private const string DefaultStartupUrl = "about:blank";
    private bool _disposed;
    private bool _overlayVisible = true;

    public OverlayCoordinator(
        OverlayWindow window,
        CefOverlayBrowser browser,
        OverlayRenderer renderer,
        OverlayModeService modeService,
        GameWindowTracker tracker,
        Win32WindowStyles windowStyles,
        OverlayPipeServer pipeServer,
        IOverlayLogger logger)
    {
        _window = window;
        _browser = browser;
        _renderer = renderer;
        _modeService = modeService;
        _tracker = tracker;
        _windowStyles = windowStyles;
        _pipeServer = pipeServer;
        _logger = logger;
        _browser.FrameReady += OnBrowserFrameReady;
        _modeService.UiModeChanged += OnUiModeChanged;
        _tracker.StateChanged += OnTrackerStateChanged;
        _tracker.GameExited += OnGameExited;
    }

    public void Start()
    {
        EnsureWindowHandle();
        var hwnd = _window.GetWindowHandle();
        _windowStyles.ApplyOverlayWindowStyle(hwnd);
        _windowStyles.ShowWithoutActivation(hwnd);

        _tracker.Start();
        ApplyWindowState(_tracker.CurrentState);

        _browser.Initialize(DefaultStartupUrl, _tracker.CurrentState.PixelWidth, _tracker.CurrentState.PixelHeight);
        ApplyUiMode(enabled: false);
        _pipeServer.Start();

        _logger.Info($"[Overlay] Started. StartupUrl={DefaultStartupUrl}");
    }

    public Task HandleCommandAsync(OverlayCommand command)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            switch (command)
            {
                case SetInteractiveCommand interactive:
                    if (interactive.Enabled)
                        _modeService.EnableUiMode();
                    else
                        _modeService.DisableUiMode();
                    break;

                case SetClickThroughCommand clickThrough:
                    if (clickThrough.Enabled)
                        _modeService.DisableUiMode();
                    else
                        _modeService.EnableUiMode();
                    break;

                case ToggleInteractiveCommand:
                    _modeService.ToggleUiMode();
                    break;

                case NavigateCommand navigate:
                    ShowOverlay();
                    _browser.Navigate(navigate.Url);
                    break;

                case NavigateHtmlCommand navigateHtml:
                    ShowOverlay();
                    _browser.NavigateToHtml(navigateHtml.Html);
                    break;

                case SetTokenCommand token:
                    _browser.SetSessionToken(token.Token);
                    break;

                case PostMessageCommand message:
                    _browser.PostGameMessageJson(message.Json);
                    break;

                case ShowOverlayCommand:
                    ShowOverlay();
                    break;

                case HideOverlayCommand:
                    HideOverlay();
                    break;

                case RunDiagnosticsCommand:
                    ShowOverlay();
                    _browser.Navigate(DefaultStartupUrl);
                    _modeService.EnableUiMode();
                    break;

                case UnknownOverlayCommand unknown:
                    _logger.Warn($"[Overlay] Unknown command: {unknown.Raw}");
                    break;
            }
        }).GetTask();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _browser.FrameReady -= OnBrowserFrameReady;
        _modeService.UiModeChanged -= OnUiModeChanged;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _tracker.GameExited -= OnGameExited;
        _pipeServer.Dispose();
        _tracker.Dispose();
    }

    private void OnBrowserFrameReady(object? sender, BrowserFrameReadyEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _renderer.Render(e));
    }

    private void OnUiModeChanged(object? sender, bool enabled)
    {
        Dispatcher.UIThread.Post(() => ApplyUiMode(enabled));
    }

    private void OnTrackerStateChanged(object? sender, GameWindowState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyWindowState(state);
            _browser.Resize(state.PixelWidth, state.PixelHeight);
        });
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.Warn("[Overlay] The game exited; closing the overlay.");
            _window.Close();
        });
    }

    private void ApplyUiMode(bool enabled)
    {
        var hwnd = EnsureWindowHandle();
        if (enabled)
        {
            ShowOverlay();
            _windowStyles.SetClickThrough(hwnd, enabled: false);
            _browser.SendFocusEvent(true);
            _window.Activate();
            _window.Focus();
            _windowStyles.FocusWindow(hwnd);
            _logger.Info("[Mode] UI mode ON => overlay captures input");
            return;
        }

        _browser.SendFocusEvent(false);
        _windowStyles.SetClickThrough(hwnd, enabled: true);
        _windowStyles.FocusGameWindow(_tracker.CurrentState.Handle);
        _logger.Info("[Mode] UI mode OFF => overlay is click-through and the game regains input");
    }

    private void ApplyWindowState(GameWindowState state)
    {
        _window.UpdateBounds(state.Left, state.Top, state.DipWidth, state.DipHeight);

        if (!_overlayVisible || state.IsMinimized || !state.IsVisible)
        {
            if (_window.IsVisible)
                _window.Hide();
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
            _windowStyles.ShowWithoutActivation(EnsureWindowHandle());
        }
    }

    private void ShowOverlay()
    {
        _overlayVisible = true;
        ApplyWindowState(_tracker.CurrentState);
        _logger.Info("[Overlay] SHOW");
    }

    private void HideOverlay()
    {
        _overlayVisible = false;
        if (_window.IsVisible)
            _window.Hide();
        _logger.Info("[Overlay] HIDE");
    }

    private nint EnsureWindowHandle()
    {
        var hwnd = _window.GetWindowHandle();
        if (hwnd == 0)
            throw new InvalidOperationException("Could not get the overlay window HWND.");
        return hwnd;
    }
}

