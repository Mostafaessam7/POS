using Microsoft.EntityFrameworkCore;
using POS.Common.Tenancy;

namespace POS.Common.Persistence;

/// <summary>
/// Base for every module context. Exists so the tenant query filter can be rooted at
/// the context instance.
/// </summary>
/// <remarks>
/// THIS IS THE FIX FOR A SUBTLE AND SERIOUS BUG, and the reason it is a base class
/// rather than a helper is worth understanding before changing anything here.
///
/// EF builds a model ONCE per context type and caches it for the process lifetime. A
/// query filter written as
///
///     e =&gt; e.TenantId == tenantContext.TenantId
///
/// closes over whichever <c>ITenantContext</c> INSTANCE existed when the model was
/// first built — typically during start-up migration, when no tenant is resolved. Every
/// later request is then filtered by that stale object, not its own tenant. On a
/// multi-tenant system that is a data-disclosure bug, and it is invisible in any test
/// that only ever exercises one tenant.
///
/// An earlier attempt fixed this by making the tenant ambient (<c>AsyncLocal</c>). That
/// works for sequential requests and FAILS under concurrent in-process ones: with
/// several requests sharing a caller's execution context — a <c>TestServer</c>, or any
/// in-process fan-out — an AsyncLocal write from one request is visible to another, and
/// a request ends up reading a different tenant's id. It was caught by two integration
/// tests interfering with each other; in production it would have been a rare,
/// unreproducible cross-tenant read.
///
/// Rooting the filter at <see cref="CurrentTenantId"/> avoids both. EF recognises a
/// query filter that references the DbContext instance and substitutes the CURRENT
/// context at query time, and the context is registered scoped — one per request, by
/// construction. There is no shared mutable state left to leak.
/// </remarks>
public abstract class PosDbContext(DbContextOptions options, ITenantContext tenantContext)
    : DbContext(options)
{
    /// <summary>
    /// The tenant this unit of work belongs to, read by the compiled query filter.
    /// </summary>
    /// <remarks>
    /// Throws when no tenant is resolved rather than returning <see cref="Guid.Empty"/>.
    /// An empty tenant would match nothing and look like "no data" — a silent wrong
    /// answer on a security boundary. Failing loudly turns a missing <c>[Authorize]</c>
    /// into an obvious error instead of an empty list.
    /// </remarks>
    public Guid CurrentTenantId => TenantContext.TenantId;

    /// <summary>The ambient tenant, for derived contexts that need it directly.</summary>
    protected ITenantContext TenantContext { get; } = tenantContext;
}
