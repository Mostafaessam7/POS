using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using POS.Common.Security;
using POS.SharedKernel;

namespace POS.Common.Auditing;

/// <summary>
/// Populates audit fields and converts hard deletes to soft deletes.
/// </summary>
/// <remarks>
/// Assigning CreatedAt/By by hand in each handler is the pattern that eventually
/// misses the one record somebody needs during an investigation.
///
/// Note the soft-delete conversion: calling Remove() on an ISoftDeletable entity
/// is silently reinterpreted. This is deliberate — it means a handler cannot
/// accidentally hard-delete master data that historical transactions reference.
/// </remarks>
public sealed class AuditingInterceptor(IClock clock, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;

        var now = clock.UtcNow;
        var actor = currentUser.UserId ?? Guid.Empty;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IAuditable)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = now;
                        entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = actor;
                        break;

                    case EntityState.Modified:
                        entry.Property(nameof(IAuditable.ModifiedAt)).CurrentValue = now;
                        entry.Property(nameof(IAuditable.ModifiedBy)).CurrentValue = actor;
                        // Never allow creation metadata to be rewritten.
                        entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                        entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
                        break;
                }
            }

            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable })
            {
                entry.State = EntityState.Modified;
                entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
                entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
                entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = actor;
            }
        }
    }
}
