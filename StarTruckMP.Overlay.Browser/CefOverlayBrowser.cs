using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using CefSharp.OffScreen;
using StarTruckMP.Overlay.Core.Abstractions;

namespace StarTruckMP.Overlay.Browser;

public sealed class CefOverlayBrowser : IDisposable
{
    private const int ReadyCheckAttempts = 20;
    private static readonly TimeSpan ReadyCheckDelay = TimeSpan.FromMilliseconds(100);
    private const int RedrawBurstCount = 6;
    private static readonly TimeSpan RedrawBurstDelay = TimeSpan.FromMilliseconds(50);

    private readonly object _syncRoot = new();
    private readonly IOverlayLogger _logger;
    private readonly Action<string> _sendMessageToGame;
    private readonly ConcurrentQueue<string> _pendingScripts = new();
    private ChromiumWebBrowser? _browser;
    private string? _sessionToken;
    private bool _mainFrameReady;
    private bool _hasReceivedJavascriptMessage;
    private int _navigationVersion;
    private int _bridgeInjectedVersion;
    private bool _disposed;

    public CefOverlayBrowser(IOverlayLogger logger, Action<string> sendMessageToGame)
    {
        _logger = logger;
        _sendMessageToGame = sendMessageToGame;
    }

    public event EventHandler<BrowserFrameReadyEventArgs>? FrameReady;

    public IBrowserHost? BrowserHost => _browser?.GetBrowserHost();

    public int PixelWidth { get; private set; } = 1280;

    public int PixelHeight { get; private set; } = 720;

    public void Initialize(string initialUrl, int pixelWidth, int pixelHeight)
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            if (_browser != null)
                return;

            PixelWidth = Math.Max(1, pixelWidth);
            PixelHeight = Math.Max(1, pixelHeight);

            var browserSettings = new BrowserSettings
            {
                WindowlessFrameRate = 60,
                BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0)
            };

            _browser = new ChromiumWebBrowser(initialUrl, browserSettings: browserSettings)
            {
                Size = new Size(PixelWidth, PixelHeight),
                RequestHandler = new OverlayRequestHandler(() => _sessionToken)
            };

            _browser.Paint += OnBrowserPaint;
            _browser.BrowserInitialized += OnBrowserInitialized;
            _browser.LoadingStateChanged += OnLoadingStateChanged;
            _browser.FrameLoadEnd += OnFrameLoadEnd;
            _browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
            _browser.ConsoleMessage += OnConsoleMessage;

            _logger.Info($"[Browser] Chromium created at {PixelWidth}x{PixelHeight}. Initial URL={initialUrl}");
        }
    }

    public void SetSessionToken(string token)
    {
        _sessionToken = token;
        _logger.Info($"[Browser] Session token updated. Length={token.Length}");
    }

    public void Navigate(string url)
    {
        var browser = RequireBrowser();
        ResetNavigationState();
        _logger.Info($"[Browser] Navigate => {url}");
        browser.Load(url);
    }

    public void NavigateToHtml(string html, string url = "https://overlay.local/")
    {
        var browser = RequireBrowser();
        ResetNavigationState();
        _logger.Info($"[Browser] NavigateToHtml => {url} ({html.Length} chars)");
        browser.LoadHtml(html, url);
    }

    public void Resize(int pixelWidth, int pixelHeight)
    {
        var browser = RequireBrowser();
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        if (pixelWidth == PixelWidth && pixelHeight == PixelHeight)
            return;

        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _logger.Info($"[Browser] Resize => {PixelWidth}x{PixelHeight}");
        browser.Size = new Size(PixelWidth, PixelHeight);
        BrowserHost?.WasResized();
    }

    public void SendFocusEvent(bool focused)
    {
        var host = BrowserHost;
        if (host == null)
        {
            _logger.Warn($"[Browser] SendFocusEvent({focused}) ignored: BrowserHost is not ready yet.");
            return;
        }

        _logger.Info($"[Browser] SendFocusEvent({focused})");
        host.SendFocusEvent(focused);
    }

    public void PostGameMessageJson(string json)
    {
        var script = $$"""
            (() => {
                const message = {{json}};
                window.__starTruckMP = window.__starTruckMP || {};
                const bridge = window.__starTruckMP;

                if (typeof bridge.dispatchGameMessage === 'function') {
                    bridge.dispatchGameMessage(message);
                    return;
                }

                bridge.pendingGameMessages = bridge.pendingGameMessages || [];
                bridge.pendingGameMessages.push(message);
            })();
            """;

        ExecuteScript(script);
    }

    public void ExecuteScript(string script)
    {
        var browser = RequireBrowser();
        if (!Volatile.Read(ref _mainFrameReady))
        {
            if (Volatile.Read(ref _hasReceivedJavascriptMessage) && browser.CanExecuteJavascriptInMainFrame)
            {
                Volatile.Write(ref _mainFrameReady, true);
                ExecuteScriptAndRequestRedraw(browser, script);
                return;
            }

            _pendingScripts.Enqueue(script);
            ScheduleMainFrameReadyCheck(_navigationVersion, browser.Address);
            return;
        }

        ExecuteScriptAndRequestRedraw(browser, script);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_browser == null)
            return;

        _browser.Paint -= OnBrowserPaint;
        _browser.BrowserInitialized -= OnBrowserInitialized;
        _browser.LoadingStateChanged -= OnLoadingStateChanged;
        _browser.FrameLoadEnd -= OnFrameLoadEnd;
        _browser.JavascriptMessageReceived -= OnJavascriptMessageReceived;
        _browser.ConsoleMessage -= OnConsoleMessage;
        _browser.Dispose();
        _browser = null;
    }

    private void OnBrowserInitialized(object? sender, EventArgs e)
    {
        _logger.Info("[Browser] BrowserInitialized");
    }

    private void OnLoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
    {
        if (!e.IsLoading)
        {
            TryMarkMainFrameReady(e.Browser?.MainFrame?.Url ?? string.Empty);
            ScheduleMainFrameReadyCheck(_navigationVersion, e.Browser?.MainFrame?.Url ?? string.Empty);
        }
    }

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        if (!e.Frame.IsValid)
        {
            _logger.Warn($"[Browser] Main frame became invalid after load. Url={e.Url}");
            ScheduleMainFrameReadyCheck(_navigationVersion, e.Url);
            return;
        }

        TryMarkMainFrameReady(e.Url);
        ScheduleMainFrameReadyCheck(_navigationVersion, e.Url);
    }

    private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            var json = JsonSerializer.Serialize(e.Message);
            Volatile.Write(ref _hasReceivedJavascriptMessage, true);

            if (!Volatile.Read(ref _mainFrameReady))
            {
                Volatile.Write(ref _mainFrameReady, true);
                FlushPendingScripts();
            }

            _sendMessageToGame(json);
        }
        catch (Exception ex)
        {
            _logger.Error("[Browser] Failed to serialize a JS message.", ex);
        }
    }

    private void OnConsoleMessage(object? sender, ConsoleMessageEventArgs e)
    {
        _logger.Info($"[BrowserConsole] {e.Source}:{e.Line} {e.Message}");
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        var length = e.Width * e.Height * 4;
        var copy = GC.AllocateUninitializedArray<byte>(length);
        Marshal.Copy(e.BufferHandle, copy, 0, length);
        FrameReady?.Invoke(this, new BrowserFrameReadyEventArgs(copy, e.Width, e.Height, e.Width * 4));
    }

    private void FlushPendingScripts()
    {
        var browser = _browser;
        if (browser == null || !browser.CanExecuteJavascriptInMainFrame)
            return;

        Volatile.Write(ref _mainFrameReady, true);

        while (_pendingScripts.TryDequeue(out var script))
        {
            try
            {
                ExecuteScriptAndRequestRedraw(browser, script);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Browser] Failed to execute a pending script: {ex.Message}");
            }
        }
    }

    private void ResetNavigationState()
    {
        Volatile.Write(ref _mainFrameReady, false);
        Volatile.Write(ref _hasReceivedJavascriptMessage, false);
        Interlocked.Increment(ref _navigationVersion);
    }

    private void TryMarkMainFrameReady(string url)
    {
        var browser = _browser;
        if (browser == null)
            return;

        try
        {
            if (!browser.CanExecuteJavascriptInMainFrame)
                return;

            var navigationVersion = _navigationVersion;
            if (_bridgeInjectedVersion != navigationVersion)
            {
                browser.EvaluateScriptAsync(BridgeScript);
                Volatile.Write(ref _bridgeInjectedVersion, navigationVersion);
                RequestViewRedrawBurst();
            }

            Volatile.Write(ref _mainFrameReady, true);
            FlushPendingScripts();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Browser] Failed to prepare the main frame for JavaScript. Url={url}. {ex.Message}");
        }
    }

    private void ScheduleMainFrameReadyCheck(int navigationVersion, string url)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= ReadyCheckAttempts; attempt++)
            {
                if (_disposed || navigationVersion != Volatile.Read(ref _navigationVersion) || Volatile.Read(ref _mainFrameReady))
                    return;

                await Task.Delay(ReadyCheckDelay).ConfigureAwait(false);

                if (_disposed || navigationVersion != Volatile.Read(ref _navigationVersion) || Volatile.Read(ref _mainFrameReady))
                    return;

                TryMarkMainFrameReady(url);
            }
        });
    }

    private void ExecuteScriptAndRequestRedraw(ChromiumWebBrowser browser, string script)
    {
        var task = browser.EvaluateScriptAsync(script);
        task.ContinueWith(_ => RequestViewRedrawBurst(), TaskScheduler.Default);
    }

    private void RequestViewRedraw()
    {
        try
        {
            BrowserHost?.Invalidate(PaintElementType.View);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Browser] Failed to invalidate the off-screen view: {ex.Message}");
        }
    }

    private void RequestViewRedrawBurst()
    {
        RequestViewRedraw();

        _ = Task.Run(async () =>
        {
            for (var i = 1; i < RedrawBurstCount; i++)
            {
                if (_disposed)
                    return;

                await Task.Delay(RedrawBurstDelay).ConfigureAwait(false);

                if (_disposed)
                    return;

                RequestViewRedraw();
            }
        });
    }

    private ChromiumWebBrowser RequireBrowser()
    {
        ThrowIfDisposed();
        return _browser ?? throw new InvalidOperationException("The browser has not been initialized yet.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private const string BridgeScript = """
        (() => {
            window.__starTruckMP = window.__starTruckMP || {};
            const bridge = window.__starTruckMP;
            bridge.pendingGameMessages = bridge.pendingGameMessages || [];
            bridge.handlers = bridge.handlers || [];
            bridge.dispatchGameMessage = bridge.dispatchGameMessage || function(message) {
                if (!bridge.handlers || bridge.handlers.length === 0) {
                    bridge.pendingGameMessages.push(message);
                    return;
                }

                for (const handler of bridge.handlers) {
                    handler(message);
                }
            };

            window.chrome = window.chrome || {};
            window.chrome.webview = window.chrome.webview || {
                postMessage: function(content) {
                    if (window.CefSharp && typeof window.CefSharp.PostMessage === 'function') {
                        window.CefSharp.PostMessage(content);
                        return;
                    }
                    console.warn('[StarTruckMP] CefSharp.PostMessage is not available yet.');
                }
            };
        })();
        """;
}

