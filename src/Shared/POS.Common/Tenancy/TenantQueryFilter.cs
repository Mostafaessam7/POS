using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using POS.SharedKernel;

namespace POS.Common.Tenancy;

/// <summary>
/// Applies the combined tenant + soft-delete query filter to every entity type,
/// centrally, by reflection.
/// </summary>
/// <remarks>
/// CRITICAL — the reason this is one method and not two:
///
///   builder.HasQueryFilter(e =&gt; e.TenantId == tenant.Id);
///   builder.HasQueryFilter(e =&gt; !e.IsDeleted);       // REPLACES the first!
///
/// On EF Core 9, a second HasQueryFilter call OVERWRITES the first rather than
/// combining. The code above leaves you with soft-delete filtering and NO TENANT
/// FILTERING. It compiles. Tests pass unless you specifically test for it. Tenant
/// data leaks silently. This has caused real breaches in real products.
///
/// Applying centrally rather than per-entity also makes the DEFAULT SAFE: a new
/// entity is filtered because it implements the marker interface, not because
/// somebody remembered. Forgetting is the failure mode; design it out.
///
/// (EF Core 10 reportedly introduces named query filters which would relax this.
/// Verify current behaviour before depending on either.)
/// </remarks>
public static class TenantQueryFilter
{
    /// <summary>Applies the combined filter, reading the tenant from the context instance.</summary>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="context">
    /// The context itself, NOT an <see cref="ITenantContext"/>. The filter expression
    /// must be rooted here so EF substitutes the current instance per query — see
    /// <see cref="Persistence.PosDbContext"/> for why closing over the tenant context
    /// instead is a cross-tenant disclosure bug.
    /// </param>
    public static void ApplyTo(ModelBuilder modelBuilder, Persistence.PosDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var isTenantScoped = typeof(ITenantScoped).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);

            if (!isTenantScoped && !isSoftDeletable)
                continue;

            var parameter = Expression.Parameter(clrType, "e");
            Expression? predicate = null;

            if (isTenantScoped)
            {
                // e => e.TenantId == thisContext.CurrentTenantId
                //
                // Rooted at the DbContext so EF rebinds it to whichever context is
                // running the query. The model is cached for the process lifetime; the
                // context is scoped per request. That difference is the entire reason
                // this expression is built by hand rather than written as a lambda that
                // closes over a captured tenant.
                predicate = Expression.Equal(
                    Expression.Property(parameter, nameof(ITenantScoped.TenantId)),
                    Expression.Property(
                        Expression.Constant(context),
                        nameof(Persistence.PosDbContext.CurrentTenantId)));
            }

            if (isSoftDeletable)
            {
                var notDeleted = Expression.Not(
                    Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));

                predicate = predicate is null
                    ? notDeleted
                    : Expression.AndAlso(predicate, notDeleted);
            }

            modelBuilder
                .Entity(clrType)
                .HasQueryFilter(Expression.Lambda(predicate!, parameter));
        }
    }
}
