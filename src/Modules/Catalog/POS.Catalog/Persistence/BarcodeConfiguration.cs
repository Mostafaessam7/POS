using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Catalog.Domain;

namespace POS.Catalog.Persistence;

/// <summary>
/// Demonstrates the FILTERED UNIQUE INDEX rule that soft delete forces on us.
/// </summary>
public sealed class BarcodeConfiguration : IEntityTypeConfiguration<Barcode>
{
    public void Configure(EntityTypeBuilder<Barcode> builder)
    {
        builder.ToTable("Barcodes");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Value).HasMaxLength(48).IsRequired();
        builder.Property(b => b.Symbology).HasConversion<int>();

        // ------------------------------------------------------------------
        // THE RULE: every unique index on a soft-deletable table MUST be filtered.
        //
        // Without `WHERE IsDeleted = 0`, a barcode can never be reused after a
        // product is removed. Merchants reuse barcodes constantly — a supplier
        // discontinues a line and the code is reassigned within a season. An
        // unfiltered index turns that into "this barcode already exists" against a
        // product the user cannot even see.
        //
        // Scoped to TENANT, not global: different suppliers reuse codes, and
        // merchants generate their own internal ones.
        //
        // There is an integration test asserting that EVERY unique index on an
        // ISoftDeletable entity carries this filter. It exists because this is
        // exactly the kind of rule applied correctly to eight tables and forgotten
        // on the ninth.
        // ------------------------------------------------------------------
        builder.HasIndex(b => new { b.TenantId, b.Value })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("UX_Barcodes_Tenant_Value");

        // The scan path. Covering index: the till resolves a scanned string to a
        // variant on every single line item, and this is the hottest read in the
        // entire product.
        builder.HasIndex(b => new { b.TenantId, b.Value, b.VariantId })
               .HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("IX_Barcodes_Scan");

        // Exactly one primary barcode per variant.
        builder.HasIndex(b => new { b.VariantId, b.IsPrimary })
               .IsUnique()
               .HasFilter("[IsPrimary] = 1 AND [IsDeleted] = 0")
               .HasDatabaseName("UX_Barcodes_Variant_Primary");

        builder.HasOne<ProductVariant>()
               .WithMany()
               .HasForeignKey(b => b.VariantId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
