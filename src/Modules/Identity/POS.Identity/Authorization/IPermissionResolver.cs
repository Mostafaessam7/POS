namespace POS.Identity.Authorization;

/// <summary>
/// Resolves a user's effective permissions at a given scope.
/// </summary>
/// <remarks>
/// WHY NOT EMBED PERMISSIONS IN THE TOKEN:
///
/// With 200+ permissions across several scopes, embedding the full set bloats the
/// JWT past comfortable header limits — and every terminal sends it on every
/// request. A compressed bitfield fits, but revocation then waits for token expiry:
/// up to 15 minutes during which a just-dismissed employee retains refund rights.
///
/// Instead the token carries only a permission VERSION. Permissions load from Redis
/// (L2) behind a short in-memory cache (L1). Changing a user's roles bumps the
/// version, invalidating instantly. The cost is a sub-millisecond warm-cache read;
/// for a system that authorises cash refunds, immediate revocation is worth far
/// more than those microseconds. See ADR 013.
/// </remarks>
public interface IPermissionResolver
{
    public Task<PermissionSet> ResolveAsync(Guid userId, int permissionVersion, CancellationToken cancellationToken);

    /// <summary>Drops the cached set. Called when roles change.</summary>
    public Task InvalidateAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>A user's permissions, indexed by the scope at which each was granted.</summary>
public sealed class PermissionSet
{
    private readonly Dictionary<string, HashSet<Guid>> _byPermission;

    public PermissionSet(int version, Dictionary<string, HashSet<Guid>> byPermission)
    {
        Version = version;
        _byPermission = byPermission;
    }

    public int Version { get; }

    public static PermissionSet Empty(int version) => new(version, []);

    /// <summary>Does the user hold this permission at this specific scope?</summary>
    public bool Has(string permissionCode, Guid scopeId) =>
        _byPermission.TryGetValue(permissionCode, out var scopes)
        && (scopes.Contains(scopeId) || scopes.Contains(Guid.Empty));

    /// <summary>Does the user hold this permission at any scope? For menu visibility only.</summary>
    public bool HasAnywhere(string permissionCode) => _byPermission.ContainsKey(permissionCode);

    /// <summary>Scopes at which the permission is held. Used to filter list queries.</summary>
    public IReadOnlySet<Guid> ScopesFor(string permissionCode) =>
        _byPermission.TryGetValue(permissionCode, out var scopes) ? scopes : new HashSet<Guid>();

    /// <summary>
    /// A defensive copy of the underlying grant, for round-tripping through a cache
    /// serializer (see <c>CachedPermissionResolver</c>'s L2). Internal because nothing
    /// outside the resolver's own caching should ever need the raw shape — every real
    /// caller belongs on <see cref="Has"/>/<see cref="HasAnywhere"/>/<see cref="ScopesFor"/>.
    /// </summary>
    internal Dictionary<string, HashSet<Guid>> ToMutableDictionary() =>
        _byPermission.ToDictionary(kv => kv.Key, kv => new HashSet<Guid>(kv.Value), StringComparer.Ordinal);
}
