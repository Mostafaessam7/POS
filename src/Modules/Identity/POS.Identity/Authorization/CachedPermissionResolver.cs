using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using POS.Identity.Domain;
using POS.Identity.Persistence;

namespace POS.Identity.Authorization;

/// <summary>
/// Resolves effective permissions from the database behind a two-level cache.
/// </summary>
/// <remarks>
/// THE CACHE KEY CARRIES THE VERSION, and that is the whole revocation design. The
/// access token states which permission version it was minted under; changing a
/// user's roles bumps <see cref="User.PermissionVersion"/>, so the very next request
/// asks for a key that has never been cached and reloads. There is no invalidation
/// message to lose and no window during which a dismissed employee keeps refund
/// rights. See ADR 013.
///
/// <see cref="InvalidateAsync"/> exists for the administrative path — an operator
/// forcing a reload after fixing role data by hand — not for correctness.
///
/// TWO LEVELS, per ADR 013:
///   L1 — <see cref="IMemoryCache"/>, per-instance, sub-millisecond, short TTL.
///   L2 — <see cref="IDistributedCache"/>, shared across every instance behind the
///        same Redis (or, absent one, the in-process stand-in <c>AddDistributedMemoryCache</c>
///        registers — see <c>IdentityModule.AddIdentityModule</c>). This is what makes a
///        cold L1 on instance B able to reuse instance A's already-loaded permission set
///        instead of hitting the database again, and it is the seam ADR 013 originally
///        described as deferred.
/// Both levels are keyed identically (userId + version), so which one answers a given
/// request changes only performance, never correctness: an L2 entry for an old version
/// is simply never looked up again once a user's version has moved on.
///
/// Query filters are bypassed deliberately. Authorization runs before a tenant is
/// established on some paths, and the user id in the token has already been
/// authenticated; filtering by an unresolved tenant would deny every request.
///
/// L2 IS BEST-EFFORT, NEVER LOAD-BEARING FOR AVAILABILITY. Every call into it is
/// wrapped and treated as a miss on failure — a network blip, Redis briefly down, a
/// misconfigured connection string — because a cache exists to make the fast path
/// faster, and an optimisation that can take down every permission check in the
/// system the moment its backing store hiccups has stopped being an optimisation.
/// Found and fixed after Redis being configured-but-unreachable in a local
/// environment turned every permission-gated endpoint into a 500 for the full
/// 5-second connection timeout, on every single request.
/// </remarks>
public sealed class CachedPermissionResolver(
    IdentityDbContext db, IMemoryCache l1Cache, IDistributedCache l2Cache, ILogger<CachedPermissionResolver> logger)
    : IPermissionResolver
{
    /// <summary>
    /// Short. The version key makes this cache correct, not fresh — the TTL only
    /// bounds memory for users who have stopped making requests.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task<PermissionSet> ResolveAsync(
        Guid userId,
        int permissionVersion,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(userId, permissionVersion);

        if (l1Cache.TryGetValue(key, out PermissionSet? cached) && cached is not null)
            return cached;

        var fromL2 = await TryGetFromL2Async(key, cancellationToken);

        if (fromL2 is not null)
        {
            var deserialized = Deserialize(fromL2);
            l1Cache.Set(key, deserialized, Lifetime);
            return deserialized;
        }

        var resolved = await LoadAsync(userId, permissionVersion, cancellationToken);

        l1Cache.Set(key, resolved, Lifetime);

        await TrySetL2Async(key, resolved, cancellationToken);

        return resolved;
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken cancellationToken)
    {
        // The version-keyed entries expire on their own; this only drops the current
        // one so an administrative fix is visible immediately rather than in five
        // minutes. Versions are monotonic, so nothing older can be resurrected.
        var version = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PermissionVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var key = CacheKey(userId, version);

        l1Cache.Remove(key);

        try
        {
            await l2Cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            // A stale L2 entry left behind by a failed removal is harmless: it is
            // keyed on the OLD version, which ResolveAsync will never look up again
            // once PermissionVersion has moved on. Losing this call to an
            // unreachable cache must not fail the administrative action that
            // triggered it (e.g. a role edit).
            logger.LogWarning(ex, "L2 permission cache unavailable during invalidation for user {UserId}; ignoring.", userId);
        }
    }

    /// <summary>A miss and an unreachable cache look identical to the caller: both fall through to the database.</summary>
    private async Task<byte[]?> TryGetFromL2Async(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await l2Cache.GetAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L2 permission cache unavailable on read for key {CacheKey}; falling back to the database.", key);
            return null;
        }
    }

    /// <summary>Failing to POPULATE the cache is never worse than the cache not existing — the next call just reloads.</summary>
    private async Task TrySetL2Async(string key, PermissionSet value, CancellationToken cancellationToken)
    {
        try
        {
            await l2Cache.SetAsync(
                key,
                Serialize(value),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L2 permission cache unavailable on write for key {CacheKey}; continuing without it.", key);
        }
    }

    private async Task<PermissionSet> LoadAsync(
        Guid userId,
        int permissionVersion,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
            return PermissionSet.Empty(permissionVersion);

        // Assignment -> role -> permission ids -> permission codes, in two round trips
        // rather than one per role. A cashier holds two or three roles; a regional
        // manager holds a dozen, and per-role queries would show up on the login path.
        var assignments = user.RoleAssignments
            .Select(a => new { a.RoleId, a.ScopeId })
            .ToList();

        if (assignments.Count == 0)
            return PermissionSet.Empty(permissionVersion);

        var roleIds = assignments.Select(a => a.RoleId).Distinct().ToList();

        var roles = await db.Roles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        var permissionIds = roles.SelectMany(r => r.PermissionIds).Distinct().ToList();

        var codesById = await db.Permissions
            .AsNoTracking()
            .Where(p => permissionIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);

        var byPermission = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        foreach (var assignment in assignments)
        {
            var role = roles.Find(r => r.Id == assignment.RoleId);

            if (role is null)
                continue;

            foreach (var permissionId in role.PermissionIds)
            {
                if (!codesById.TryGetValue(permissionId, out var code))
                    continue;

                if (!byPermission.TryGetValue(code, out var scopes))
                {
                    scopes = [];
                    byPermission[code] = scopes;
                }

                scopes.Add(assignment.ScopeId);
            }
        }

        return new PermissionSet(permissionVersion, byPermission);
    }

    private static string CacheKey(Guid userId, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"perm:{userId:N}:{version}");

    private static byte[] Serialize(PermissionSet set) =>
        JsonSerializer.SerializeToUtf8Bytes(new SerializedPermissionSet(set.Version, set.ToMutableDictionary()));

    private static PermissionSet Deserialize(byte[] bytes)
    {
        var payload = JsonSerializer.Deserialize<SerializedPermissionSet>(bytes)!;
        return new PermissionSet(payload.Version, payload.ByPermission);
    }

    /// <summary>The wire shape for L2 — <see cref="PermissionSet"/> itself has no public constructor shaped for round-tripping arbitrary JSON.</summary>
    private sealed record SerializedPermissionSet(int Version, Dictionary<string, HashSet<Guid>> ByPermission);
}
