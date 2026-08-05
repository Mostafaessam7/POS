using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Sales.Domain;
using POS.SharedKernel;

namespace POS.Sales.Persistence;

/// <summary>
/// Shared money mapping for this module.
/// </summary>
/// <remarks>
/// <c>decimal(19,4)</c> everywhere, never float — binary floating point cannot
/// represent 0.10, and a day's trading accumulates the error into a drawer that will
/// not balance. Currency is <c>char(3)</c>: it is an ISO 4217 code, it is fixed
/// length, and fixed length indexes better than varchar.
/// </remarks>
internal static class MoneyMapping
{
    public static void MapMoney<T>(
        this ComplexPropertyBuilder<T> builder,
        string columnPrefix)
        where T : struct
    {
        builder.Property(nameof(Money.Amount)).HasColumnName($"{columnPrefix}Amount").HasPrecision(19, 4);
        builder.Property(nameof(Money.Currency)).HasColumnName($"{columnPrefix}Currency").HasMaxLength(3).IsFixedLength();
    }
}

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(s => s.Status).HasConversion<int>();
        builder.Property(s => s.RowVersion).IsRowVersion();

        // The receipt number is a value object but is queried directly — "bring up
        // receipt A-000412" is the single most common support action — so it is
        // flattened onto the row rather than owned in a side table.
        builder.ComplexProperty(s => s.ReceiptNumber, r =>
        {
            r.Property(n => n.Series).HasColumnName("ReceiptSeries").HasMaxLength(20);
            r.Property(n => n.Sequence).HasColumnName("ReceiptSequence");
        });

        builder.ComplexProperty(s => s.TotalExclusiveTax, m => m.MapMoney("TotalExclusiveTax"));
        builder.ComplexProperty(s => s.TotalTax, m => m.MapMoney("TotalTax"));
        builder.ComplexProperty(s => s.TotalInclusiveTax, m => m.MapMoney("TotalInclusiveTax"));
        builder.ComplexProperty(s => s.TotalDiscount, m => m.MapMoney("TotalDiscount"));
        builder.ComplexProperty(s => s.RoundingAdjustment, m => m.MapMoney("RoundingAdjustment"));
        builder.ComplexProperty(s => s.AmountTendered, m => m.MapMoney("AmountTendered"));
        builder.ComplexProperty(s => s.ChangeGiven, m => m.MapMoney("ChangeGiven"));

        // Optional, so owned rather than complex: EF Core 9 complex properties cannot
        // be null, and most sales reverse nothing.
        builder.OwnsOne(s => s.Reverses, r =>
        {
            // The shadow key is declared explicitly. By convention EF would adopt
            // SaleReference.SaleId as this owned type's key — its name matches the
            // owner — and then fail, because the owned type shares the Sales table and
            // its key must be the owner's Id column. SaleId here is the id of the sale
            // being REVERSED, which is a different sale entirely.
            r.WithOwner().HasForeignKey("Id");

            r.Property(x => x.SaleId).HasColumnName("ReversesSaleId");
            r.Property(x => x.ReceiptNumber).HasColumnName("ReversesReceiptNumber").HasMaxLength(30);
            r.Property(x => x.BusinessDate).HasColumnName("ReversesBusinessDate");
        });

        builder.HasMany(s => s.Lines)
               .WithOne()
               .HasForeignKey("SaleId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Tenders)
               .WithOne()
               .HasForeignKey("SaleId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Tenders).UsePropertyAccessMode(PropertyAccessMode.Field);

        // UX_Sales_Receipt IS NOT DECLARED HERE, and this one is a correctness
        // constraint rather than a performance index — read the migration before
        // changing anything about ReceiptNumber.
        //
        // Receipt numbers are gap-free per terminal (ADR 005). The unique index is the
        // GUARANTEE; the allocator is only the mechanism, and a mechanism without a
        // constraint behind it fails the first time two terminals are restored from
        // the same disk image and start minting the same numbers.
        //
        // Series and Sequence are members of the ReceiptNumber complex property, and
        // EF Core 9's HasIndex accepts only a simple property access, so the index is
        // created as explicit SQL in InitialSales. Demoting ReceiptNumber to an owned
        // type would let EF own the index but not one spanning TenantId and TerminalId,
        // which are what make it per-terminal.

        // Z-report and daily takings: every sale for a terminal on a business date.
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.BusinessDate })
               .HasDatabaseName("IX_Sales_BusinessDate");

        builder.HasIndex(s => new { s.TenantId, s.ShiftId })
               .HasDatabaseName("IX_Sales_Shift");

        // Suspended sales are recalled by the next cashier and must not require a scan.
        builder.HasIndex(s => new { s.TenantId, s.TerminalId, s.Status })
               .HasDatabaseName("IX_Sales_Status");

        builder.Ignore(s => s.DomainEvents);
        builder.Ignore(s => s.BalanceDue);
    }
}

public sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> builder)
    {
        builder.ToTable("SaleLines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Description).HasMaxLength(256).IsRequired();
        builder.Property(l => l.UnitOfMeasure).HasMaxLength(20).IsRequired();
        builder.Property(l => l.TaxCode).HasMaxLength(20).IsRequired();

        // Quantity is decimal, not int: weighed goods sell in fractional kilograms and
        // a 0.325 kg line must be representable exactly.
        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.TaxRate).HasPrecision(9, 4);

        builder.ComplexProperty(l => l.UnitPrice, m => m.MapMoney("UnitPrice"));
        builder.ComplexProperty(l => l.UnitCostAtSale, m => m.MapMoney("UnitCostAtSale"));
        builder.ComplexProperty(l => l.DiscountAmount, m => m.MapMoney("Discount"));
        builder.ComplexProperty(l => l.NetAmount, m => m.MapMoney("Net"));
        builder.ComplexProperty(l => l.TaxAmount, m => m.MapMoney("Tax"));
        builder.ComplexProperty(l => l.GrossAmount, m => m.MapMoney("Gross"));

        // The audit trail for "why was this £4.37?" (ADR 034).
        //
        // A DEPENDENT ENTITY TYPE, not an owned one, and the reason is a hard EF Core 9
        // limitation rather than a modelling preference: owned types cannot contain
        // complex properties, and PriceAdjustment carries a Money. Owned would force
        // the amount into a single column and throw the currency away — on the one
        // table whose entire purpose is explaining a disputed figure.
        //
        // The key is (SaleLineId, Sequence). Sequence is assigned by the pricing
        // pipeline and is already unique within a line, so no surrogate is invented.
        builder.HasMany(l => l.Adjustments)
               .WithOne()
               .HasForeignKey("SaleLineId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(l => l.Adjustments).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(l => l.VariantId).HasDatabaseName("IX_SaleLines_Variant");

        builder.Ignore(l => l.Currency);
        builder.Ignore(l => l.Margin);
    }
}

/// <inheritdoc cref="SaleLineConfiguration"/>
public sealed class PriceAdjustmentConfiguration : IEntityTypeConfiguration<PriceAdjustment>
{
    public void Configure(EntityTypeBuilder<PriceAdjustment> builder)
    {
        builder.ToTable("SaleLineAdjustments");

        builder.Property<Guid>("SaleLineId");
        builder.HasKey("SaleLineId", nameof(PriceAdjustment.Sequence));

        builder.Property(a => a.Stage).HasConversion<int>();
        builder.Property(a => a.Description).HasMaxLength(200).IsRequired();

        builder.ComplexProperty(a => a.Amount, m => m.MapMoney(""));

        // "Which manager authorised this override, and how often?" is a fraud query,
        // and it needs to be cheap from day one rather than after an incident.
        builder.HasIndex(a => a.AuthorisedBy).HasDatabaseName("IX_SaleLineAdjustments_AuthorisedBy");
    }
}

public sealed class TenderConfiguration : IEntityTypeConfiguration<Tender>
{
    public void Configure(EntityTypeBuilder<Tender> builder)
    {
        builder.ToTable("Tenders");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Method).HasConversion<int>();
        builder.Property(t => t.Reference).HasMaxLength(100);

        builder.ComplexProperty(t => t.Amount, m => m.MapMoney(""));

        // Sale <-> Payment reconciliation walks this both ways.
        builder.HasIndex(t => t.PaymentId).HasDatabaseName("IX_Tenders_Payment");
    }
}

public sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("Shifts");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(s => s.Status).HasConversion<int>();
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.ComplexProperty(s => s.OpeningFloat, m => m.MapMoney("OpeningFloat"));
        builder.ComplexProperty(s => s.CountedCash, m => m.MapMoney("CountedCash"));
        builder.ComplexProperty(s => s.ExpectedCash, m => m.MapMoney("ExpectedCash"));
        builder.ComplexProperty(s => s.Variance, m => m.MapMoney("Variance"));

        builder.HasMany(s => s.CashMovements)
               .WithOne()
               .HasForeignKey("ShiftId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.CashMovements).UsePropertyAccessMode(PropertyAccessMode.Field);

        // At most one open shift per terminal. Filtered so that the thousands of
        // closed shifts are not in the index, and so a terminal can open a new shift
        // the moment the previous one closes.
        builder.HasIndex(s => new { s.TenantId, s.TerminalId })
               .IsUnique()
               .HasFilter("[Status] = 0")
               .HasDatabaseName("UX_Shifts_OpenPerTerminal");

        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.BusinessDate })
               .HasDatabaseName("IX_Shifts_BusinessDate");

        builder.Ignore(s => s.DomainEvents);
        builder.Ignore(s => s.HasVariance);
    }
}

public sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable("CashMovements");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Kind).HasConversion<int>();
        builder.Property(c => c.Reference).HasMaxLength(100);

        builder.ComplexProperty(c => c.Amount, m => m.MapMoney(""));
    }
}
