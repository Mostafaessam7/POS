using Microsoft.EntityFrameworkCore;
using POS.Catalog.Domain;
using POS.Common.Persistence;
using POS.Common.Tenancy;

namespace POS.Catalog.Persistence;

public sealed class CatalogDbContext(
    DbContextOptions<CatalogDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "catalog";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Catalog";

    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> Variants => Set<ProductVariant>();
    public DbSet<Barcode> Barcodes => Set<Barcode>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<TaxGroup> TaxGroups => Set<TaxGroup>();

    /// <summary>
    /// The variant vocabulary. Exposed as sets because nothing navigates to them —
    /// <c>VariantAttributeValue</c> holds bare ids — so without a DbSet they would not
    /// be discovered by the model builder and the typed-attribute design would have no
    /// tables at all.
    /// </summary>
    public DbSet<VariantAttribute> VariantAttributes => Set<VariantAttribute>();

    public DbSet<VariantAttributeOption> VariantAttributeOptions => Set<VariantAttributeOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}
