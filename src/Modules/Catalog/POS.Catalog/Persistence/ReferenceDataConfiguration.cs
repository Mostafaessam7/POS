using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Catalog.Domain;

namespace POS.Catalog.Persistence;

public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("Brands");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();

        builder.HasIndex(b => new { b.TenantId, b.Name })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");

        builder.Ignore(b => b.DomainEvents);
    }
}

public sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        builder.ToTable("UnitsOfMeasure");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Code).HasMaxLength(20).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(100).IsRequired();

        // A case of 12 is factor 12; a gram against a kilogram is 0.001. Six decimal
        // places so a derived unit's factor does not lose precision when a quantity is
        // converted back to base for the stock ledger.
        builder.Property(u => u.ConversionFactor).HasPrecision(18, 6);

        builder.HasIndex(u => new { u.TenantId, u.Code })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");

        builder.HasOne<UnitOfMeasure>()
               .WithMany()
               .HasForeignKey(u => u.BaseUnitId)
               // Restrict: deleting "Each" must not cascade away "Case of 12", which
               // is what every product's pack size is expressed in.
               .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(u => u.DomainEvents);
    }
}

/// <summary>
/// The variant axes — Size, Colour — and their permitted values.
/// </summary>
/// <remarks>
/// These are stored as tables rather than a JSON blob on the variant, which is the
/// decision ADR 022's sibling reasoning covers: merchants filter and report by size
/// and colour, and "every Large in Blue across the chain" against a JSON column is a
/// full scan. <c>ProductVariantAttributes</c> holds the assignment; these two hold
/// the vocabulary it points at.
/// </remarks>
public sealed class VariantAttributeConfiguration : IEntityTypeConfiguration<VariantAttribute>
{
    public void Configure(EntityTypeBuilder<VariantAttribute> builder)
    {
        builder.ToTable("VariantAttributes");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Name).HasMaxLength(100).IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.Name }).IsUnique();
    }
}

public sealed class VariantAttributeOptionConfiguration : IEntityTypeConfiguration<VariantAttributeOption>
{
    public void Configure(EntityTypeBuilder<VariantAttributeOption> builder)
    {
        builder.ToTable("VariantAttributeOptions");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Value).HasMaxLength(100).IsRequired();

        builder.HasIndex(o => new { o.TenantId, o.AttributeId, o.Value }).IsUnique();

        builder.HasOne<VariantAttribute>()
               .WithMany()
               .HasForeignKey(o => o.AttributeId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
