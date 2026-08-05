using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Purchasing.Domain;

namespace POS.Purchasing.Persistence;

/// <summary>The Purchasing module's persistence boundary.</summary>
/// <remarks>
/// Five aggregates, deliberately separate: a supplier outlives every order placed
/// against it, an order outlives its receipts, and an invoice arrives days after the
/// goods. Modelling them as one graph would make receiving a delivery load the
/// supplier's entire trading history.
///
/// They reference each other by id, never by navigation, which is also what keeps the
/// three-way match a pure function over three documents rather than a traversal.
/// </remarks>
public sealed class PurchasingDbContext(
    DbContextOptions<PurchasingDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "purchasing";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Purchasing";

    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<SupplierReturn> SupplierReturns => Set<SupplierReturn>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PurchasingDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}
