using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using POS.SharedKernel;

namespace POS.Common.Tenancy;

/// <summary>
/// Layer 2 of tenant enforcement: writes.
/// </summary>
/// <remarks>
/// Query filters protect READS only. This interceptor assigns TenantId on insert
/// from the ambient context, and THROWS on any attempt to modify an entity
/// belonging to a different tenant.
///
/// It catches the case a query filter cannot: an entity loaded under one context
/// and saved under another, which is exactly what happens in a background job or a
/// mis-scoped DI registration.
///
/// A violation is a hard failure, not a warning. There is no scenario in which
/// silently writing across a security boundary is preferable to an exception.
/// </remarks>
public sealed class TenantGuardInterceptor(
    ITenantContext tenantContext,
    ILogger<TenantGuardInterceptor> logger) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Guard(DbContext? context)
    {
        if (context is null || !tenantContext.IsResolved)
            return;

        if (((TenantContext)tenantContext).IsBypassed)
            return;

        var tenantId = tenantContext.TenantId;

        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    AssignTenant(entry, tenantId);
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    if (entry.Entity.TenantId != tenantId)
                        throw CrossTenantWrite(entry, tenantId);
                    break;
            }
        }
    }

    private void AssignTenant(EntityEntry<ITenantScoped> entry, Guid tenantId)
    {
        var property = entry.Property(nameof(ITenantScoped.TenantId));

        // An explicitly-supplied, non-matching TenantId is an attack or a bug.
        // Either way it must not be silently corrected.
        if (property.CurrentValue is Guid existing && existing != Guid.Empty && existing != tenantId)
            throw CrossTenantWrite(entry, tenantId);

        property.CurrentValue = tenantId;
    }

    // A blocked write is a security event: it is logged before it is thrown, because
    // the exception may be swallowed further up but the audit trail must not be.
    private InvalidOperationException CrossTenantWrite(EntityEntry entry, Guid expected)
    {
        var entityType = entry.Entity.GetType().Name;
        var actual = ((ITenantScoped)entry.Entity).TenantId;

        logger.LogError(
            "Cross-tenant write blocked. Entity={EntityType} Expected={ExpectedTenantId} Actual={ActualTenantId}",
            entityType,
            expected,
            actual);

        return new InvalidOperationException(
            $"Cross-tenant write blocked. Entity={entityType} Expected={expected} Actual={actual}");
    }
}
