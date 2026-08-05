using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Catalog.Domain;

namespace POS.Catalog.Persistence;

public sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> builder)
    {
        builder.ToTable("PriceLists");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // THE price resolution query: which lists apply to this branch, for this
        // customer group, right now — ordered by priority. It runs on every line of
        // every sale, so it is the one index in this module that must not be missed.
        builder.HasIndex(p => new { p.TenantId, p.BranchId, p.EffectiveFrom, p.EffectiveTo })
               .HasDatabaseName("IX_PriceLists_Effective");

        // Entries are part of the list, not independently addressable: a price only
        // means anything in the context of the list and version that produced it
        // (ADR 023), so it is owned rather than a related aggregate.
        builder.OwnsMany(p => p.Entries, entry =>
        {
            entry.ToTable("PriceListEntries");
            entry.WithOwner().HasForeignKey(e => e.PriceListId);
            entry.HasKey(e => new { e.PriceListId, e.VariantId });

            // decimal(19,4) is the money standard throughout — never float.
            entry.Property(e => e.Amount).HasPrecision(19, 4);

            entry.HasIndex(e => e.VariantId).HasDatabaseName("IX_PriceListEntries_Variant");
        });

        builder.Navigation(p => p.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(p => p.DomainEvents);
    }
}

public sealed class TaxGroupConfiguration : IEntityTypeConfiguration<TaxGroup>
{
    public void Configure(EntityTypeBuilder<TaxGroup> builder)
    {
        builder.ToTable("TaxGroups");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(50).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.Code }).IsUnique();

        builder.HasMany(t => t.Rates)
               .WithOne()
               .HasForeignKey(r => r.TaxGroupId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Rates).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.DomainEvents);
    }
}

public sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable("TaxRates");
        builder.HasKey(r => r.Id);

        // Whole percentage points, e.g. 14.0000 for 14%. Four decimal places because
        // fractional rates exist (7.5%, 8.25%) and rounding the RATE compounds into
        // every line it is applied to.
        builder.Property(r => r.Percentage).HasPrecision(9, 4);

        // Resolving "the rate in force at this instant" for a group. A rate change is
        // legislated in advance, so the open-ended row is not always the current one.
        builder.HasIndex(r => new { r.TenantId, r.TaxGroupId, r.EffectiveFrom })
               .HasDatabaseName("IX_TaxRates_Effective");
    }
}
