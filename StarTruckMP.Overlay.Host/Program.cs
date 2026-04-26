using Avalonia;
using Avalonia.Controls;
using StarTruckMP.Overlay.Host.Models;

namespace StarTruckMP.Overlay.Host;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var launchOptions = ParseArgs(args);

        BuildAvaloniaApp(launchOptions)
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

        return 0;
    }

    private static AppBuilder BuildAvaloniaApp(LaunchOptions launchOptions)
    {
        return AppBuilder.Configure(() => new OverlayApp(launchOptions))
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static LaunchOptions ParseArgs(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Expected two arguments: <gameHwnd> <gamePid>");

        if (!long.TryParse(args[0], out var hwndValue))
            throw new ArgumentException($"Invalid HWND: '{args[0]}'");

        if (!int.TryParse(args[1], out var processId))
            throw new ArgumentException($"Invalid PID: '{args[1]}'");

        return new LaunchOptions(new IntPtr(hwndValue), processId);
    }
}


