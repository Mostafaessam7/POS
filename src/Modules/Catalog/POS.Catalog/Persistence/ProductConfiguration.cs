using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Catalog.Domain;

namespace POS.Catalog.Persistence;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.Kind).HasConversion<int>();

        // Backing-field access: variants are exposed as IReadOnlyList (architecture
        // rule 7) so callers cannot append a variant without going through the
        // aggregate, which is where the SKU uniqueness invariant lives.
        builder.Metadata
               .FindNavigation(nameof(Product.Variants))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(p => p.Variants)
               .WithOne()
               .HasForeignKey(v => v.ProductId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.TenantId, p.CategoryId })
               .HasFilter("[IsDeleted] = 0");

        // Back-office product search.
        builder.HasIndex(p => new { p.TenantId, p.Name })
               .HasFilter("[IsDeleted] = 0");
    }
}

public sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("ProductVariants");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Sku).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(256).IsRequired();

        // decimal(19,4) is the money standard throughout. NEVER float or real:
        // binary floating point cannot represent 0.10, and a day's trading
        // accumulates the error into a drawer that will not balance.
        builder.Property(v => v.DefaultPriceAmount).HasPrecision(19, 4);
        builder.Property(v => v.AverageCost).HasPrecision(19, 4);
        builder.Property(v => v.DefaultPriceCurrency).HasMaxLength(3).IsFixedLength();

        builder.HasIndex(v => new { v.TenantId, v.Sku })
               .IsUnique()
               .HasDatabaseName("UX_ProductVariants_Tenant_Sku");

        builder.OwnsMany(v => v.Attributes, a =>
        {
            a.ToTable("ProductVariantAttributes");
            a.WithOwner().HasForeignKey(x => x.VariantId);
            a.HasKey(x => new { x.VariantId, x.AttributeId });
        });
    }
}
