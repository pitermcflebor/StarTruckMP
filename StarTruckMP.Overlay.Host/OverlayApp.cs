using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using StarTruckMP.Overlay.Browser;
using StarTruckMP.Overlay.Core.Services;
using StarTruckMP.Overlay.Host.Interop;
using StarTruckMP.Overlay.Host.Models;
using StarTruckMP.Overlay.Host.Services;
using StarTruckMP.Overlay.Host.UI;

namespace StarTruckMP.Overlay.Host;

internal sealed class OverlayApp : Application
{
    private readonly LaunchOptions _launchOptions;
    private ConsoleOverlayLogger? _logger;
    private CefOverlayBrowser? _browser;
    private OverlayCoordinator? _coordinator;
    private BrowserInputRouter? _inputRouter;
    private HotkeyService? _hotkeyService;

    public OverlayApp(LaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _logger = new ConsoleOverlayLogger();
            _logger.Info($"[App] Launching overlay. GameHwnd=0x{_launchOptions.GameWindowHandle:X}, GamePid={_launchOptions.GameProcessId}");

            CefRuntimeBootstrapper.IgnoreCertificateErrorsByDefault = _launchOptions.IgnoreCertificateErrors;
            CefRuntimeBootstrapper.InitializeOnce(_logger);

            var window = new OverlayWindow();
            var modeService = new OverlayModeService(_logger);
            var renderer = new OverlayRenderer(window, _logger);
            var pipeToGame = new GamePipeClient(_logger);
            _browser = new CefOverlayBrowser(_logger, pipeToGame.SendJsonLine);
            var tracker = new GameWindowTracker(_launchOptions.GameWindowHandle, _launchOptions.GameProcessId, _logger);
            var windowStyles = new Win32WindowStyles(_logger);

            _coordinator = new OverlayCoordinator(
                window,
                _browser,
                renderer,
                modeService,
                tracker,
                windowStyles,
                pipeToGame,
                new OverlayPipeServer(_logger, command => _coordinator!.HandleCommandAsync(command)),
                _logger);

            _hotkeyService = new HotkeyService(window, modeService, _logger);
            _inputRouter = new BrowserInputRouter(window, _browser, modeService, _logger);

            desktop.MainWindow = window;
            desktop.Startup += (_, _) => _coordinator.Start();
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _logger?.Info("[App] Closing overlay.");
        _inputRouter?.Dispose();
        _hotkeyService?.Dispose();
        _coordinator?.Dispose();
        _browser?.Dispose();
        if (_logger != null)
            CefRuntimeBootstrapper.Shutdown(_logger);
    }
}

