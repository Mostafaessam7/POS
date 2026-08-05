using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace POS.Common.Tenancy;

/// <summary>
/// Reads the tenant from the signed token and, where the route also carries a
/// tenant identifier for URL readability, validates that the two agree.
/// </summary>
/// <remarks>
/// The route value is VALIDATED AGAINST the claim, never READ FROM. Three lines
/// that close an entire vulnerability class.
/// </remarks>
public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ILogger<TenantResolutionMiddleware> logger)
{
    public const string TenantClaimType = Security.PosClaimTypes.Tenant;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var claim = context.User.FindFirstValue(TenantClaimType);

        if (!Guid.TryParse(claim, out var tenantId))
        {
            logger.LogWarning(
                "Authenticated principal without a valid tenant claim. Subject={Subject}",
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (context.Request.RouteValues.TryGetValue("tenantId", out var routeValue)
            && Guid.TryParse(routeValue?.ToString(), out var routeTenantId)
            && routeTenantId != tenantId)
        {
            logger.LogWarning(
                "Tenant mismatch between route and token. Route={RouteTenant} Token={TokenTenant} Path={Path}",
                routeTenantId, tenantId, context.Request.Path);

            // 404, not 403: a 403 confirms the resource exists, which is itself a
            // small information leak across a security boundary.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ((TenantContext)tenantContext).Resolve(tenantId);

        await next(context);
    }
}
