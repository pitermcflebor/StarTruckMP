using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using StarTruckMP.Server.Controllers.Filters;

namespace StarTruckMP.Server.Controllers;

[ApiController]
[Route("overlay")]
[RequireSessionToken]
public class OverlayController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    // Shared, thread-safe provider for file-extension → MIME-type resolution.
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public OverlayController(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// Serves the SvelteKit static assets (JS, CSS, fonts…) when an exact file match
    /// is found under <c>wwwroot/overlay</c>, and falls back to <c>index.html</c> for
    /// all other paths so the client-side router can handle navigation.
    /// </summary>
    [HttpGet("{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Serve(string? path)
    {
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var overlayRoot = Path.GetFullPath(Path.Combine(webRoot, "overlay"));

        // Attempt to serve a physical static file (JS, CSS, images, fonts, etc.)
        if (!string.IsNullOrEmpty(path))
        {
            var filePath = Path.GetFullPath(Path.Combine(overlayRoot, path));

            // Guard against path-traversal attacks before touching the file system.
            if (filePath.StartsWith(overlayRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(filePath))
            {
                ContentTypeProvider.TryGetContentType(filePath, out var mimeType);
                return PhysicalFile(filePath, mimeType ?? "application/octet-stream");
            }
        }

        // SPA fallback: return index.html so the Svelte router handles the route.
        var indexPath = Path.Combine(overlayRoot, "index.html");
        if (!System.IO.File.Exists(indexPath))
            return NotFound("Overlay UI has not been built yet.");

        return PhysicalFile(indexPath, "text/html");
    }
}
