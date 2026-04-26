using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using StarTruckMP.Server.Controllers.Services;

namespace StarTruckMP.Server.Controllers.Filters;

/// <summary>
/// Requires a valid session token in the <c>X-Session-Token</c> request header.
/// Uses <see cref="AuthService"/> to validate the token.
/// Returns <c>401 Unauthorized</c> when the header is missing or the token is invalid/expired.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireSessionTokenAttribute : Attribute, IAuthorizationFilter
{
    private const string HeaderName = "X-Session-Token";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var authService = context.HttpContext.RequestServices.GetRequiredService<AuthService>();

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var rawValue))
        {
            context.Result = new UnauthorizedObjectResult($"Missing '{HeaderName}' header.");
            return;
        }

        var token = rawValue.ToString();

        if (!authService.IsTokenValid(token))
            context.Result = new UnauthorizedObjectResult("Invalid or expired session token.");
    }
}


