using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using POS.Identity.Authorization;
using POS.Identity.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Proves <see cref="CachedPermissionResolver"/>'s L2 (<see cref="IDistributedCache"/>)
/// genuinely serves a permission resolution rather than merely existing unused —
/// ADR 013's originally-deferred scale gap.
/// </summary>
/// <remarks>
/// The fixture registers the in-process <c>AddDistributedMemoryCache</c> stand-in, not
/// a real Redis, because there is no Redis in this test environment. That is fine for
/// what this test proves: it is the SAME <see cref="IDistributedCache"/> abstraction
/// <see cref="CachedPermissionResolver"/> talks to either way, so a correct code path
/// here is a correct code path against a real Redis too — the two backends differ
/// only in whether the state is shared across processes, not in how the resolver
/// uses them.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class DistributedPermissionCacheTests(ApiFixture fixture)
{
    /// <summary>
    /// The scenario ADR 013's L2 exists for: instance A resolves and populates both
    /// cache levels; instance B's L1 is cold (a fresh process would never have it),
    /// but its L2 is the same shared store, so it must answer from there instead of
    /// re-querying the database.
    /// </summary>
    [Fact]
    public async Task A_permission_resolution_survives_an_L1_cache_miss_by_reading_L2()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (_, userId) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, "diagnostic.l2cache.probe");

        using var scope = fixture.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var l1 = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var l2 = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var version = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.PermissionVersion)
            .FirstAsync();

        // First resolution: a real DB load, populating both L1 and L2.
        var firstResolution = await resolver.ResolveAsync(userId, version, CancellationToken.None);
        firstResolution.HasAnywhere("diagnostic.l2cache.probe").ShouldBeTrue();

        // Revoke the grant DIRECTLY IN THE DATABASE, bypassing PermissionVersion —
        // the same "reach around the API" stance ApiFixture.AttemptDirectCrossTenantWriteAsync
        // takes, here to prove what L2 is doing rather than what the ORM does. If the
        // resolver were to reload from the database now, it would see this and return
        // an EMPTY set; if it correctly serves L2 instead, it must still return the
        // grant, because nothing has told L2 to forget it.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE [identity].[Roles] SET PermissionIds = '[]' WHERE TenantId = {org.TenantId}");

        // Drop L1 ONLY — simulating a cold instance that never had this entry, the
        // exact case a single-instance in-process cache cannot cover.
        l1.Remove($"perm:{userId:N}:{version}");

        var secondResolution = await resolver.ResolveAsync(userId, version, CancellationToken.None);

        secondResolution.HasAnywhere("diagnostic.l2cache.probe").ShouldBeTrue();

        // And L2 itself really does hold a serialized entry under this key — not just
        // an assertion that happens to pass because L1 quietly repopulated everything.
        var l2Entry = await l2.GetAsync($"perm:{userId:N}:{version}");
        l2Entry.ShouldNotBeNull();
    }

    /// <summary>Invalidation must clear BOTH levels, or a dropped L1 would resurrect a stale L2 entry.</summary>
    [Fact]
    public async Task Invalidating_a_users_permissions_clears_both_cache_levels()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (_, userId) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, "diagnostic.l2cache.invalidate");

        using var scope = fixture.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var l2 = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var version = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.PermissionVersion)
            .FirstAsync();

        await resolver.ResolveAsync(userId, version, CancellationToken.None);
        (await l2.GetAsync($"perm:{userId:N}:{version}")).ShouldNotBeNull();

        await resolver.InvalidateAsync(userId, CancellationToken.None);

        (await l2.GetAsync($"perm:{userId:N}:{version}")).ShouldBeNull();
    }

    /// <summary>
    /// Found by actually running this against a Redis connection string pointed at
    /// nothing: every permission-gated request took the connection's full 5-second
    /// timeout and then failed with a 500, because L2 being unreachable was treated
    /// as a fault instead of a cache miss. L2 is an optimisation; an optimisation
    /// that can take the whole system down when its backing store is unavailable
    /// has stopped being one.
    /// </summary>
    [Fact]
    public async Task An_unreachable_L2_degrades_to_a_database_load_instead_of_failing_the_request()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (_, userId) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, "diagnostic.l2cache.unreachable");

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var l1 = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

        var version = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.PermissionVersion)
            .FirstAsync();

        var resolver = new CachedPermissionResolver(
            db, l1, new AlwaysThrowingDistributedCache(), NullLogger<CachedPermissionResolver>.Instance);

        var resolved = await resolver.ResolveAsync(userId, version, CancellationToken.None);

        resolved.HasAnywhere("diagnostic.l2cache.unreachable").ShouldBeTrue();
    }

    /// <summary>Stands in for Redis being configured but unreachable — every call fails, exactly like a connection timeout would.</summary>
    private sealed class AlwaysThrowingDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Unreachable() => new("Simulated: L2 cache is unreachable.");

        public byte[]? Get(string key) => throw Unreachable();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Unreachable();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Unreachable();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Unreachable();
        public void Refresh(string key) => throw Unreachable();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Unreachable();
        public void Remove(string key) => throw Unreachable();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Unreachable();
    }
}
