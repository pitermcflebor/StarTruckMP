using CefSharp;
using CefSharp.Handler;

namespace StarTruckMP.Overlay.Browser;

internal sealed class OverlayRequestHandler : RequestHandler
{
    private readonly Func<string?> _sessionTokenProvider;

    public OverlayRequestHandler(Func<string?> sessionTokenProvider)
    {
        _sessionTokenProvider = sessionTokenProvider;
    }

    protected override IResourceRequestHandler? GetResourceRequestHandler(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        bool isNavigation,
        bool isDownload,
        string requestInitiator,
        ref bool disableDefaultHandling)
    {
        return new OverlayResourceRequestHandler(_sessionTokenProvider);
    }
}

