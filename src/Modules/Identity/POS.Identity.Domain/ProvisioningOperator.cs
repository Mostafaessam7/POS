using System.Security.Cryptography;
using System.Text;
using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A named, individually revocable credential for the platform's own operations
/// tooling — the identity behind a call to the tenant-bootstrap endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a single secret shared by everyone with operator access
/// (<c>Provisioning:OperatorApiKey</c>, ADR-era design) with named, hashed-at-rest
/// rows, each independently revocable — the same shape <see cref="RefreshToken"/>
/// uses for a login session, applied to the platform's own bootstrap credential
/// instead of a user's. A leaked key now identifies WHICH operator to revoke and
/// WHOSE tenants to review, rather than forcing everyone with access to rotate a
/// secret they all shared.
/// </para>
/// <para>
/// Deliberately NOT <see cref="ITenantScoped"/>. Provisioning a tenant is the one
/// operation that necessarily happens before any tenant exists, so the credential
/// authorizing it cannot itself belong to one — the same reasoning that keeps
/// <see cref="Permission"/> and <see cref="Tenant"/> off the tenant boundary.
/// </para>
/// <para>
/// Operators are minted and revoked through their own root-gated endpoints
/// (<c>POST/GET /provisioning/operators</c>), not through this system's ordinary
/// permission model — there is no tenant-scoped user to hold a permission before a
/// tenant exists. The root key that gates THOSE endpoints
/// (<c>Provisioning:RootApiKey</c>) is deliberately harder to reach day-to-day than
/// an individual operator key: it mints and revokes operator identities, but never
/// itself provisions a tenant.
/// </para>
/// </remarks>
public sealed class ProvisioningOperator : Entity<Guid>
{
    private ProvisioningOperator() { }

    public static ProvisioningOperator Enrol(string name, string keyHash, DateTimeOffset now) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        KeyHash = keyHash,
        CreatedAt = now
    };

    public string Name { get; private set; } = null!;

    /// <summary>SHA-256 of the operator's key, hex-encoded. The plaintext is handed back exactly once, at creation, and is never persisted.</summary>
    public string KeyHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    /// <summary>Idempotent: revoking an already-revoked operator does not move the timestamp.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    /// <summary>The same hashing shape <c>TokenService.HashRefreshToken</c> uses for refresh tokens.</summary>
    public static string HashKey(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
