#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace StarTruckMP.Client.UI;

internal static class OverlayManager
{
    #region Win32

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    #endregion

    private const string PipeName     = "StarTruckMP.Overlay";
    private const string GamePipeName = "StarTruckMP.Client";

    private static Process? _overlayProcess;
    private static CancellationTokenSource? _gamePipeCts;
    private static volatile bool _interactiveMode;

    #region Events

    /// <summary>
    /// Raised on a background thread whenever the Svelte overlay calls
    /// <c>sendToGame(type, payload)</c> from JavaScript.
    /// </summary>
    /// <remarks>
    /// <c>type</c>    — the event type string set in JavaScript.<br/>
    /// <c>payload</c> — raw JSON of the payload, or <c>null</c> if omitted.
    /// </remarks>
    public static event Action<string, string?>? MessageReceived;

    #endregion

    #region Public API

    /// <summary>Locate, launch and connect to the overlay process.</summary>
    public static void Launch()
    {
        // Start the game pipe server before the overlay process so it is
        // ready to accept connections as soon as the overlay boots.
        StartGamePipeServer();

        Plugin.StartAttachedThread(() =>
        {
            var exe = ResolveOverlayExe();
            if (exe == null)
            {
                App.Log.LogWarning("[Overlay] StarTruckMP.Overlay.exe not found. Searched:");
                App.Log.LogWarning($"[Overlay]   {Path.Combine(Path.GetDirectoryName(typeof(OverlayManager).Assembly.Location) ?? "", "StarTruckMP.Overlay.exe")}");
                App.Log.LogWarning($"[Overlay]   {Path.Combine(Path.GetDirectoryName(typeof(OverlayManager).Assembly.Location) ?? "", "overlay", "StarTruckMP.Overlay.exe")}");
                return;
            }

            App.Log.LogInfo($"[Overlay] Found exe at: {exe}");

            var hwnd = WaitForGameWindow(timeoutMs: 30_000);
            var pid  = Process.GetCurrentProcess().Id;
            App.Log.LogInfo($"[Overlay] Game HWND={hwnd}, PID={pid}");

            // Pass HWND and PID so the overlay can track the game window and
            // automatically exit when the game process terminates.
            var psi = new ProcessStartInfo(exe, $"{hwnd.ToInt64()} {pid}")
            {
                UseShellExecute = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };

            try
            {
                _overlayProcess = Process.Start(psi);
                if (_overlayProcess == null)
                {
                    App.Log.LogError("[Overlay] Process.Start returned null — the OS refused to start the process.");
                    return;
                }

                App.Log.LogInfo($"[Overlay] Process started (pid={_overlayProcess.Id}).");

                // Pipe stdout/stderr from the overlay to the BepInEx log.
                _overlayProcess.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        App.Log.LogInfo($"[Overlay:out] {e.Data}");
                };
                _overlayProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        App.Log.LogWarning($"[Overlay:err] {e.Data}");
                };
                _overlayProcess.BeginOutputReadLine();
                _overlayProcess.BeginErrorReadLine();

                // Detect early crash.
                _overlayProcess.Exited += (_, _) =>
                    App.Log.LogWarning($"[Overlay] Process exited with code {_overlayProcess.ExitCode}.");
                _overlayProcess.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                App.Log.LogError($"[Overlay] Failed to start process: {ex.Message}");
            }
        });
    }

    /// <summary>Kill the overlay process (called on plugin unload if needed).</summary>
    public static void Shutdown()
    {
        _gamePipeCts?.Cancel();
        try
        {
            if (_overlayProcess is { HasExited: false })
                _overlayProcess.Kill();
        }
        catch { /* ignored */ }
    }

    #endregion

    #region IPC helpers

    /// <summary>Enable or disable click-through on the overlay window.</summary>
    public static void SetClickThrough(bool enabled)
    {
        _interactiveMode = !enabled;
        SendCommand($"CLICKTHROUGH:{(enabled ? "1" : "0")}");
    }

    public static bool IsInteractiveMode => _interactiveMode;

    public static void SetInteractiveMode(bool enabled)
    {
        _interactiveMode = enabled;
        SendCommand($"INTERACTIVE:{(enabled ? "1" : "0")}");
    }

    public static void ToggleInteractiveMode()
    {
        _interactiveMode = !_interactiveMode;
        SendCommand("TOGGLEINTERACTIVE");
    }

    public static void RunDiagnostics() => SendCommand("RUNTESTS");

    /// <summary>Navigates the CEF browser to a URL.</summary>
    public static void Navigate(string url) =>
        SendCommand($"NAVIGATE:{url}");

    /// <summary>Loads raw HTML into CEF.</summary>
    public static void NavigateToHtml(string html) =>
        SendCommand($"NAVHTML:{Convert.ToBase64String(Encoding.UTF8.GetBytes(html))}");

    /// <summary>Shows the CEF overlay.</summary>
    public static void Show() => SendCommand("SHOW");

    /// <summary>Hides the overlay while keeping the window alive.</summary>
    public static void Hide() => SendCommand("HIDE");

    /// <summary>
    /// Sends the session token to the overlay and immediately navigates to the given
    /// URL — both in a single pipe write so the token is guaranteed to be stored
    /// before the navigation request fires.
    /// </summary>
    public static void SetSessionTokenAndNavigate(string token, string url)
    {
        SendCommands($"TOKEN:{token}", $"NAVIGATE:{url}");
        App.Log.LogInfo($"[Overlay] Navigated to url (with token): {url}.");
    }

    /// <summary>
    /// Posts a typed message to the SvelteKit page via <c>window.postMessage</c>.
    /// The page can listen with <c>onGameMessage</c> from <c>$lib/gameEvents</c>.
    /// </summary>
    /// <param name="type">Arbitrary event type string (e.g. "playerSpeed").</param>
    /// <param name="jsonPayload">
    /// Optional JSON-serialized payload object (e.g. <c>"{\"value\":50}"</c>).
    /// Pass <c>null</c> to omit the payload field entirely.
    /// </param>
    public static void PostMessage(string type, string? jsonPayload = null)
    {
        // Escape only the characters that would break the JSON string literal.
        var typeEscaped = type.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var json = jsonPayload != null
            ? $"{{\"source\":\"StarTruckMP\",\"type\":\"{typeEscaped}\",\"payload\":{jsonPayload}}}"
            : $"{{\"source\":\"StarTruckMP\",\"type\":\"{typeEscaped}\"}}";

        SendCommand($"POSTMESSAGE:{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}");
    }

    private static JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    public static void PostMessage<T>(string type, T? data)
    {
        var payload = new
        {
            type = type.Replace("\\", "\\\\").Replace("\"", "\\\""),
            source = "StarTruckMP",
            payload = data
        };
        var json = JsonSerializer.Serialize(payload, _jsonOpt);

        SendCommand($"POSTMESSAGE:{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}");
    }

    private static void SendCommand(string command) => SendCommands(command);

    private static void SendCommands(params string[] commands)
    {
        Plugin.StartAttachedThread(() =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(timeout: 2000);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: 1024, leaveOpen: false);
                writer.AutoFlush = true;
                foreach (var cmd in commands)
                    writer.WriteLine(cmd);
            }
            catch (Exception ex)
            {
                App.Log.LogWarning($"[Overlay] IPC send failed: {ex.Message}");
            }
        });
    }

    #endregion

    #region Game window detection

    /// <summary>
    /// Enumerates top-level windows of the current process, skipping the BepInEx
    /// console window, and returns the first visible one (the Unity game window).
    /// </summary>
    private static IntPtr FindGameWindow()
    {
        var currentPid  = (uint)Process.GetCurrentProcess().Id;
        var consoleHwnd = GetConsoleWindow();
        var found       = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == consoleHwnd)    return true; // skip BepInEx console
            if (!IsWindowVisible(hwnd)) return true; // skip hidden windows

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != currentPid)      return true; // skip other processes

            found = hwnd;
            return false; // stop – first match is the game window
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Polls <see cref="FindGameWindow"/> until it returns a valid handle or the
    /// timeout expires.
    /// </summary>
    private static IntPtr WaitForGameWindow(int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;

        while (Environment.TickCount < deadline)
        {
            var hwnd = FindGameWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            Thread.Sleep(250);
        }

        return FindGameWindow(); // last resort
    }

    #endregion

    #region Helpers

    private static string? ResolveOverlayExe()
    {
        var pluginDir = Path.GetDirectoryName(typeof(OverlayManager).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDir)) return null;

        var candidate = Path.Combine(pluginDir, "overlay", "StarTruckMP.Overlay.exe");
        if (IsOverlayDeploymentValid(candidate)) return candidate;

        candidate = Path.Combine(pluginDir, "StarTruckMP.Overlay.exe");
        if (IsOverlayDeploymentValid(candidate)) return candidate;

        return null;
    }

    private static bool IsOverlayDeploymentValid(string exePath)
    {
        if (!File.Exists(exePath))
            return false;

        var directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string[] requiredFiles =
        [
            "StarTruckMP.Overlay.deps.json",
            "StarTruckMP.Overlay.runtimeconfig.json",
            "CefSharp.Core.Runtime.dll",
            "CefSharp.Core.dll",
            "CefSharp.dll",
            "CefSharp.OffScreen.dll",
            "CefSharp.BrowserSubprocess.exe",
            "libcef.dll"
        ];

        var missing = requiredFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();

        if (missing.Length == 0)
            return true;

        App.Log.LogWarning($"[Overlay] Ignoring incomplete deployment at: {exePath}");
        foreach (var file in missing)
            App.Log.LogWarning($"[Overlay]   Missing: {Path.Combine(directory, file)}");

        return false;
    }

    #endregion

    #region Game pipe server (Overlay → Plugin)

    private static void StartGamePipeServer()
    {
        _gamePipeCts = new CancellationTokenSource();
        var ct = _gamePipeCts.Token;

        Plugin.StartAttachedThread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        GamePipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // Wait for the overlay to connect; respect cancellation.
                    pipe.WaitForConnectionAsync(ct).Wait(ct);

                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    string? line;
                    while (!ct.IsCancellationRequested && (line = reader.ReadLine()) != null)
                        DispatchGameMessage(line.Trim());
                }
                catch (OperationCanceledException) { break; }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        App.Log.LogWarning($"[Overlay] Game pipe error: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        });
    }

    /// <summary>
    /// Parses a JSON line received from the overlay and raises
    /// <see cref="MessageReceived"/> with the extracted type and payload.
    /// Expected format: <c>{"type":"eventName","payload":{...}}</c>
    /// </summary>
    private static void DispatchGameMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString() ?? string.Empty;
            var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : null;
            if (type.Equals("overlayModeChanged", StringComparison.OrdinalIgnoreCase) &&
                p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("interactive", out var interactiveElement) &&
                (interactiveElement.ValueKind == JsonValueKind.True || interactiveElement.ValueKind == JsonValueKind.False))
            {
                _interactiveMode = interactiveElement.GetBoolean();
            }
            
            MessageReceived?.Invoke(type, payload);
        }
        catch (Exception ex)
        {
            App.Log.LogWarning($"[Overlay] Failed to parse game message: {ex.Message}");
        }
    }

    #endregion
}
