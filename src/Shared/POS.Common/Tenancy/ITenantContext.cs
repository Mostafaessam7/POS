namespace POS.Common.Tenancy;

/// <summary>
/// The ambient tenant for the current unit of work.
/// </summary>
/// <remarks>
/// Resolved ONLY from the signed access token. Never from a header, route
/// parameter, or subdomain — all three are caller-supplied input. A subdomain may
/// select which login page and branding to show; after authentication the claim
/// governs, and any mismatch is a security event. See ADR 006.
/// </remarks>
public interface ITenantContext
{
    /// <summary>Throws if no tenant is resolved. Use <see cref="IsResolved"/> to check first.</summary>
    public Guid TenantId { get; }

    public bool IsResolved { get; }

    /// <summary>
    /// Suspends tenant filtering for the lifetime of the returned scope.
    /// </summary>
    /// <remarks>
    /// Required by exactly three callers: the login flow (which must find a user
    /// before a tenant is known), the sync ingest pipeline (which processes batches
    /// from many tenants), and background maintenance jobs. Every use is logged.
    /// It is deliberately awkward to reach.
    /// </remarks>
    public IDisposable BypassForSystemOperation(string reason);

    /// <summary>
    /// Acts as a specific tenant for the lifetime of the returned scope.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BypassForSystemOperation"/>, and the difference is the
    /// one that matters on WRITES. A bypass suspends filtering; it does not name a
    /// tenant, so the write interceptor has nothing to stamp onto a new row and the
    /// entity is persisted with an empty TenantId — invisible to every subsequent
    /// query and belonging to nobody.
    ///
    /// This is for the operations that legitimately know which tenant they are acting
    /// for without a request having established one: provisioning a new tenant, and
    /// sync ingest, which processes a batch whose tenant it resolved itself. Both are
    /// logged, for the same reason the bypass is.
    /// </remarks>
    public IDisposable EnterTenant(Guid tenantId, string reason);
}
