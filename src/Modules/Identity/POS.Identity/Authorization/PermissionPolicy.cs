using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace POS.Identity.Authorization;

/// <summary>
/// Builds an authorization policy per permission code, on demand.
/// </summary>
/// <remarks>
/// The alternative is registering a named policy for every permission at start-up.
/// With 200+ permissions across the catalogue that is a list nobody keeps in step: a
/// new permission is added, the registration is forgotten, and the endpoint that uses
/// it fails at RUNTIME with "policy not found" — in production, on the one code path
/// nobody exercised. Generating the policy from the name it was asked for cannot drift.
///
/// The prefix keeps permission policies distinct from any hand-registered policy, so
/// this provider only claims names it owns and defers everything else to the default.
/// </remarks>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName is null || !policyName.StartsWith(Prefix, StringComparison.Ordinal))
            return await base.GetPolicyAsync(policyName!);

        var permissionCode = policyName[Prefix.Length..];

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permissionCode))
            .Build();
    }
}

/// <summary>Requires a permission on an endpoint.</summary>
public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Gates the endpoint on holding <paramref name="permissionCode"/> at ANY scope.
    /// </summary>
    /// <remarks>
    /// A COARSE GATE, and deliberately so. It answers "does this user hold this
    /// capability at all", which is the right question for routing and for hiding
    /// endpoints a user can never use. It does NOT answer "may they do it to THIS
    /// branch's data" — that depends on a value in the request, which the authorization
    /// pipeline cannot see.
    ///
    /// Handlers that act on a specific branch, company or warehouse must therefore also
    /// call <see cref="IPermissionScopeGuard"/>. Relying on this alone would let a user
    /// with supervisor rights at one branch approve an order at another.
    /// </remarks>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionCode)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RequireAuthorization($"{PermissionPolicyProvider.Prefix}{permissionCode}");
        return builder;
    }
}

/// <summary>Checks a permission at a specific authorization scope.</summary>
/// <remarks>
/// The second half of the enforcement pair described on
/// <see cref="PermissionEndpointExtensions.RequirePermission{TBuilder}"/>: the policy
/// gate proves the capability exists, this proves it reaches the branch or company the
/// request names. Both are needed, and only this one can see the request body.
/// </remarks>
public interface IPermissionScopeGuard
{
    public Task<bool> HasAtScopeAsync(string permissionCode, Guid scopeId, CancellationToken cancellationToken);

    /// <summary>The highest level held from an ordered ladder, or null if none is held.</summary>
    /// <remarks>
    /// Used by approval ladders, where the question is not "may they approve" but "how
    /// far up may they approve" — the answer the domain needs in order to compare
    /// against the document's value.
    /// </remarks>
    public Task<TLevel?> HighestHeldAsync<TLevel>(
        IReadOnlyList<(string PermissionCode, TLevel Level)> ladder,
        Guid scopeId,
        CancellationToken cancellationToken)
        where TLevel : struct;
}

/// <inheritdoc cref="IPermissionScopeGuard"/>
public sealed class PermissionScopeGuard(IPermissionResolver resolver, Common.Security.ICurrentUser currentUser)
    : IPermissionScopeGuard
{
    public async Task<bool> HasAtScopeAsync(
        string permissionCode,
        Guid scopeId,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return false;

        var permissions = await resolver.ResolveAsync(userId, currentUser.PermissionVersion, cancellationToken);
        return permissions.Has(permissionCode, scopeId);
    }

    public async Task<TLevel?> HighestHeldAsync<TLevel>(
        IReadOnlyList<(string PermissionCode, TLevel Level)> ladder,
        Guid scopeId,
        CancellationToken cancellationToken)
        where TLevel : struct
    {
        ArgumentNullException.ThrowIfNull(ladder);

        if (currentUser.UserId is not { } userId)
            return null;

        var permissions = await resolver.ResolveAsync(userId, currentUser.PermissionVersion, cancellationToken);

        TLevel? highest = null;

        // The ladder is ordered lowest to highest, so the last match wins.
        foreach (var (code, level) in ladder)
        {
            if (permissions.Has(code, scopeId))
                highest = level;
        }

        return highest;
    }
}
