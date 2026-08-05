using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Sales.Domain;

namespace POS.Sales.Persistence;

/// <summary>The Sales module's persistence boundary.</summary>
/// <remarks>
/// Sale and Shift are separate aggregates and are stored as such. A shift is not a
/// parent of its sales — it is a cash-accountability period that a sale references by
/// id. Modelling it as a parent would make closing a shift load every sale in it,
/// which on a busy till is thousands of rows to compute one drawer total.
/// </remarks>
public sealed class SalesDbContext(
    DbContextOptions<SalesDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "sales";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Sales";

    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Shift> Shifts => Set<Shift>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SalesDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}
