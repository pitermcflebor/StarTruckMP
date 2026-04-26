using CefSharp;
using CefSharp.Handler;

namespace StarTruckMP.Overlay.Browser;

internal sealed class OverlayResourceRequestHandler : ResourceRequestHandler
{
    private readonly Func<string?> _sessionTokenProvider;

    public OverlayResourceRequestHandler(Func<string?> sessionTokenProvider)
    {
        _sessionTokenProvider = sessionTokenProvider;
    }

    protected override CefReturnValue OnBeforeResourceLoad(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        IRequestCallback callback)
    {
        var token = _sessionTokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.SetHeaderByName("X-Session-Token", token, overwrite: true);
        }

        return CefReturnValue.Continue;
    }
}


