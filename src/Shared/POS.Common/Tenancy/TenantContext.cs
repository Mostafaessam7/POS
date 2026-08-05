using Microsoft.Extensions.Logging;

namespace POS.Common.Tenancy;

/// <inheritdoc cref="ITenantContext"/>
/// <remarks>
/// Ordinary instance state, registered SCOPED. There is deliberately no ambient
/// (<c>AsyncLocal</c>) storage here: an earlier version used it so that EF's cached
/// query filter would see the current request's tenant, and that leaked across
/// concurrent in-process requests — one request could read another's tenant id. The
/// query filter now reads from the scoped DbContext instead
/// (see <c>PosDbContext</c>), which removes the reason ambient state ever seemed
/// necessary.
/// </remarks>
public sealed class TenantContext(ILogger<TenantContext> logger) : ITenantContext
{
    private Guid? _tenantId;
    private bool _bypassed;

    public bool IsResolved => _tenantId.HasValue || _bypassed;

    public Guid TenantId => _tenantId
        ?? throw new InvalidOperationException(
            "No tenant resolved. Either the endpoint is missing [Authorize], or a " +
            "system operation is running without BypassForSystemOperation.");

    /// <summary>Called once per request by <c>TenantResolutionMiddleware</c>.</summary>
    internal void Resolve(Guid tenantId)
    {
        if (_tenantId.HasValue && _tenantId != tenantId)
            throw new InvalidOperationException("Tenant context reassignment attempted.");

        _tenantId = tenantId;
    }

    public IDisposable BypassForSystemOperation(string reason)
    {
        logger.LogWarning("Tenant filtering bypassed. Reason={Reason}", reason);

        _bypassed = true;
        return new BypassScope(this);
    }

    public IDisposable EnterTenant(Guid tenantId, string reason)
    {
        logger.LogWarning(
            "Acting as tenant outside a request. TenantId={TenantId} Reason={Reason}",
            tenantId,
            reason);

        var previous = _tenantId;
        _tenantId = tenantId;

        return new TenantScope(this, previous);
    }

    internal bool IsBypassed => _bypassed;

    private sealed class BypassScope(TenantContext context) : IDisposable
    {
        public void Dispose() => context._bypassed = false;
    }

    /// <remarks>
    /// Restores the previous tenant rather than clearing it, so entering a tenant
    /// inside a request that already has one leaves the request's own tenant intact.
    /// </remarks>
    private sealed class TenantScope(TenantContext context, Guid? previous) : IDisposable
    {
        public void Dispose() => context._tenantId = previous;
    }
}
