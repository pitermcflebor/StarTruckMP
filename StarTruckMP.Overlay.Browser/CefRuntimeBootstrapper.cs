using System.Reflection;
using CefSharp;
using CefSharp.OffScreen;
using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Browser;

public static class CefRuntimeBootstrapper
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;
    public static bool IgnoreCertificateErrorsByDefault { get; set; }

    public static void InitializeOnce(IOverlayLogger logger)
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                logger.Info("[CEF] Already initialized.");
                return;
            }

            var baseDirectory = AppContext.BaseDirectory;
            var ignoreCertificateErrors = IgnoreCertificateErrorsByDefault;
            var settings = new CefSettings
            {
                WindowlessRenderingEnabled = true,
                LogSeverity = LogSeverity.Info,
                BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0),
                RootCachePath = Path.Combine(baseDirectory, "cef-cache")
            };

            settings.CefCommandLineArgs["disable-gpu-vsync"] = "1";
            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
            if (ignoreCertificateErrors)
                settings.CefCommandLineArgs["ignore-certificate-errors"] = "1";

            var subProcessCandidates = new[]
            {
                Path.Combine(baseDirectory, "CefSharp.BrowserSubprocess.exe"),
                Path.Combine(baseDirectory, "x64", "CefSharp.BrowserSubprocess.exe")
            };

            foreach (var candidate in subProcessCandidates)
            {
                if (!File.Exists(candidate))
                    continue;

                settings.BrowserSubprocessPath = candidate;
                break;
            }

            logger.Info($"[CEF] Initializing runtime from {baseDirectory}");
            logger.Info($"[CEF] BrowserSubprocessPath={settings.BrowserSubprocessPath ?? "<default>"}");
            logger.Info($"[CEF] IgnoreCertificateErrors={ignoreCertificateErrors}");

            var ok = Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
            if (!ok)
                throw new InvalidOperationException("Cef.Initialize returned false.");

            _initialized = true;
            logger.Info($"[CEF] Initialized. CefSharp={typeof(Cef).Assembly.GetName().Version}, Host={Assembly.GetEntryAssembly()?.GetName().Version}");
        }
    }

    public static void Shutdown(IOverlayLogger logger)
    {
        lock (SyncRoot)
        {
            if (!_initialized)
                return;

            logger.Info("[CEF] Shutting down runtime.");
            Cef.Shutdown();
            _initialized = false;
        }
    }
}


