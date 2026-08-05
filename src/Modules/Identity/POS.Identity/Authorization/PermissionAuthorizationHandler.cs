using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using POS.Common.Security;

namespace POS.Identity.Authorization;

/// <summary>Requires a permission, optionally at the scope named by a route value.</summary>
public sealed class PermissionRequirement(string permissionCode, string? scopeRouteKey = null)
    : IAuthorizationRequirement
{
    public string PermissionCode { get; } = permissionCode;

    /// <summary>
    /// Route value naming the scope, e.g. "branchId". When null, the permission is
    /// satisfied if held at ANY scope — appropriate for list endpoints, which then
    /// filter their results to the accessible scope set.
    /// </summary>
    public string? ScopeRouteKey { get; } = scopeRouteKey;
}

public sealed class PermissionAuthorizationHandler(
    IPermissionResolver resolver,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (currentUser.UserId is not { } userId)
            return;

        var permissions = await resolver.ResolveAsync(
            userId,
            currentUser.PermissionVersion,
            httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);

        var granted = requirement.ScopeRouteKey is null
            ? permissions.HasAnywhere(requirement.PermissionCode)
            : TryGetScope(requirement.ScopeRouteKey, out var scopeId)
              && permissions.Has(requirement.PermissionCode, scopeId);

        if (granted)
            context.Succeed(requirement);

        // Deliberately no context.Fail(): another handler may satisfy the
        // requirement. Denials are logged by the middleware, which is the
        // intrusion signal worth alerting on.
    }

    private bool TryGetScope(string routeKey, out Guid scopeId)
    {
        scopeId = Guid.Empty;

        var value = httpContextAccessor.HttpContext?.Request.RouteValues[routeKey]?.ToString();
        return Guid.TryParse(value, out scopeId);
    }
}
