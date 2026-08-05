using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using POS.Purchasing.Domain;
using POS.SharedKernel;

namespace POS.Purchasing.Persistence;

/// <summary>Shared mapping conventions for this module.</summary>
/// <remarks>
/// <c>decimal(19,4)</c> for money and <c>decimal(18,4)</c> for quantity, never float:
/// binary floating point cannot represent 0.10, and a purchase ledger that drifts by a
/// penny per line does not reconcile against a supplier statement. Quantities are
/// decimal because goods arrive by weight as well as by count.
/// </remarks>
internal static class PurchasingMapping
{
    public static void MapMoney<T>(this ComplexPropertyBuilder<T> builder, string columnPrefix)
        where T : struct
    {
        builder.Property(nameof(Money.Amount)).HasColumnName($"{columnPrefix}Amount").HasPrecision(19, 4);
        builder.Property(nameof(Money.Currency)).HasColumnName($"{columnPrefix}Currency").HasMaxLength(3).IsFixedLength();
    }

    /// <summary>BusinessDate is a value object over DateOnly; the database sees a date.</summary>
    public static readonly ValueConverter<BusinessDate, DateOnly> BusinessDateConverter =
        new(date => date.Value, value => BusinessDate.Open(value));
}

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Code).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.TaxRegistrationNumber).HasMaxLength(50);
        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();

        // Terms carry no Money, so they can be owned and share the supplier's row.
        builder.OwnsOne(s => s.Terms, t =>
        {
            t.Property(x => x.PaymentTermDays).HasColumnName("PaymentTermDays");
            t.Property(x => x.LeadTimeDays).HasColumnName("LeadTimeDays");
            t.Property(x => x.MinimumOrderValue).HasColumnName("MinimumOrderValue").HasPrecision(19, 4);
        });

        builder.Navigation(s => s.Terms).IsRequired();

        // The supplier's own part number for a variant. Owned: a code has no meaning
        // apart from the supplier that issues it, and nothing addresses one directly.
        builder.OwnsMany(s => s.ProductCodes, c =>
        {
            c.ToTable("SupplierProductCodes");
            c.WithOwner().HasForeignKey("SupplierId");
            c.HasKey("SupplierId", nameof(SupplierProductCode.VariantId));
            c.Property(x => x.Code).HasMaxLength(60).IsRequired();
            c.Property(x => x.Description).HasMaxLength(200);
            c.Property(x => x.PackSize).HasPrecision(18, 4);
        });

        builder.Navigation(s => s.ProductCodes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(s => new { s.TenantId, s.CompanyId, s.Code })
               .IsUnique()
               .HasDatabaseName("UX_Suppliers_Code");

        builder.Ignore(s => s.DomainEvents);
    }
}

public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PurchaseOrders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrderNumber).HasMaxLength(30).IsRequired();
        builder.Property(o => o.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(o => o.Status).HasConversion<int>();
        builder.Property(o => o.CancellationReason).HasMaxLength(500);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Property(o => o.BusinessDate).HasConversion(PurchasingMapping.BusinessDateConverter);

        // Snapshotted at the point of raising. A supplier renegotiating terms next month
        // must not silently restate what this order was placed under.
        builder.OwnsOne(o => o.AgreedTerms, t =>
        {
            t.Property(x => x.PaymentTermDays).HasColumnName("AgreedPaymentTermDays");
            t.Property(x => x.LeadTimeDays).HasColumnName("AgreedLeadTimeDays");
            t.Property(x => x.MinimumOrderValue).HasColumnName("AgreedMinimumOrderValue").HasPrecision(19, 4);
        });

        builder.Navigation(o => o.AgreedTerms).IsRequired();

        // The separation-of-duties audit trail: who approved, at what level, and why.
        // Owned because an approval only exists in the context of its order.
        builder.OwnsMany(o => o.Approvals, a =>
        {
            a.ToTable("PurchaseOrderApprovals");
            a.WithOwner().HasForeignKey("PurchaseOrderId");
            a.HasKey("PurchaseOrderId", nameof(PurchaseOrderApproval.ApproverUserId), nameof(PurchaseOrderApproval.DecidedAt));
            a.Property(x => x.Level).HasConversion<int>();
            a.Property(x => x.Reason).HasMaxLength(500);
        });

        builder.HasMany(o => o.Lines)
               .WithOne()
               .HasForeignKey(l => l.PurchaseOrderId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(o => o.Approvals).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(o => new { o.TenantId, o.OrderNumber })
               .IsUnique()
               .HasDatabaseName("UX_PurchaseOrders_Number");

        // "What is on order from this supplier, and what is overdue?" — the buyer's
        // daily screen, and the query behind every supplier chase-up.
        builder.HasIndex(o => new { o.TenantId, o.SupplierId, o.Status, o.ExpectedDeliveryDate })
               .HasDatabaseName("IX_PurchaseOrders_Outstanding");

        builder.Ignore(o => o.DomainEvents);
        builder.Ignore(o => o.TotalValue);
        builder.Ignore(o => o.IsEditable);
        builder.Ignore(o => o.IsFullyResolved);
    }
}

public sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("PurchaseOrderLines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.QuantityOrdered).HasPrecision(18, 4);
        builder.Property(l => l.QuantityReceived).HasPrecision(18, 4);
        builder.Property(l => l.QuantityCancelled).HasPrecision(18, 4);
        builder.Property(l => l.SupplierCode).HasMaxLength(60);
        builder.Property(l => l.Description).HasMaxLength(200);
        builder.Property(l => l.CancellationReason).HasMaxLength(500);

        builder.ComplexProperty(l => l.UnitPrice, m => m.MapMoney("UnitPrice"));

        builder.HasIndex(l => l.VariantId).HasDatabaseName("IX_PurchaseOrderLines_Variant");

        builder.Ignore(l => l.OutstandingQuantity);
        builder.Ignore(l => l.OverReceivedQuantity);
        builder.Ignore(l => l.LineTotal);
    }
}

public sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        builder.ToTable("GoodsReceipts");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReceiptNumber).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(r => r.SupplierDeliveryNote).HasMaxLength(60);
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.BusinessDate).HasConversion(PurchasingMapping.BusinessDateConverter);

        // Lines and landed costs are DEPENDENT ENTITY TYPES, not owned collections, and
        // the reason is a hard EF Core 9 limitation rather than a modelling preference:
        // owned types cannot contain complex properties, and both records carry a Money.
        // Mapping them as owned would collapse the amount into one column and discard
        // the currency — on an import receipt, where the freight invoice and the goods
        // are routinely in different currencies, that is not a detail to lose.
        builder.HasMany(r => r.Lines)
               .WithOne()
               .HasForeignKey("GoodsReceiptId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.LandedCosts)
               .WithOne()
               .HasForeignKey("GoodsReceiptId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(r => r.LandedCosts).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.ReceiptNumber })
               .IsUnique()
               .HasDatabaseName("UX_GoodsReceipts_Number");

        // Receipt <-> stock ledger reconciliation walks from the order to its receipts.
        builder.HasIndex(r => new { r.TenantId, r.PurchaseOrderId })
               .HasDatabaseName("IX_GoodsReceipts_Order");

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.GoodsValue);
        builder.Ignore(r => r.LandedCostTotal);
        builder.Ignore(r => r.IsPosted);
    }
}

/// <inheritdoc cref="GoodsReceiptConfiguration"/>
public sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> builder)
    {
        builder.ToTable("GoodsReceiptLines");

        builder.Property<Guid>("GoodsReceiptId");
        builder.HasKey("GoodsReceiptId", nameof(GoodsReceiptLine.PurchaseOrderLineNumber));

        builder.Property(l => l.QuantityReceived).HasPrecision(18, 4);
        builder.ComplexProperty(l => l.UnitPrice, m => m.MapMoney("UnitPrice"));

        builder.Ignore(l => l.LineValue);
    }
}

/// <inheritdoc cref="GoodsReceiptConfiguration"/>
public sealed class LandedCostChargeConfiguration : IEntityTypeConfiguration<LandedCostCharge>
{
    public void Configure(EntityTypeBuilder<LandedCostCharge> builder)
    {
        builder.ToTable("LandedCostCharges");

        builder.Property<Guid>("GoodsReceiptId");

        // Keyed on the reference, not a surrogate: two freight charges on one receipt
        // are two different invoices, and the reference is the invoice number. A
        // duplicate reference on the same receipt is a double-posted charge, which this
        // key rejects rather than silently inflating the landed cost.
        builder.HasKey("GoodsReceiptId", nameof(LandedCostCharge.Reference));

        builder.Property(c => c.Reference).HasMaxLength(60);
        builder.Property(c => c.Type).HasConversion<int>();
        builder.Property(c => c.Basis).HasConversion<int>();

        builder.ComplexProperty(c => c.Amount, m => m.MapMoney(""));
    }
}

public sealed class PurchaseInvoiceConfiguration : IEntityTypeConfiguration<PurchaseInvoice>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoice> builder)
    {
        builder.ToTable("PurchaseInvoices");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.SupplierInvoiceNumber).HasMaxLength(60).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(i => i.Status).HasConversion<int>();
        builder.Property(i => i.BlockReason).HasMaxLength(500);
        builder.Property(i => i.RowVersion).IsRowVersion();

        builder.HasMany(i => i.Lines)
               .WithOne()
               .HasForeignKey("PurchaseInvoiceId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // A supplier's invoice number is unique PER SUPPLIER, not globally — two
        // suppliers both numbering from 1 is normal. This is the duplicate-invoice
        // control: without it the same invoice can be entered and paid twice.
        builder.HasIndex(i => new { i.TenantId, i.SupplierId, i.SupplierInvoiceNumber })
               .IsUnique()
               .HasDatabaseName("UX_PurchaseInvoices_SupplierNumber");

        // The payables run: what is approved and due.
        builder.HasIndex(i => new { i.TenantId, i.Status, i.DueDate })
               .HasDatabaseName("IX_PurchaseInvoices_Due");

        builder.Ignore(i => i.DomainEvents);
        builder.Ignore(i => i.NetTotal);
    }
}

/// <inheritdoc cref="GoodsReceiptConfiguration"/>
public sealed class PurchaseInvoiceLineConfiguration : IEntityTypeConfiguration<PurchaseInvoiceLine>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoiceLine> builder)
    {
        builder.ToTable("PurchaseInvoiceLines");

        builder.Property<Guid>("PurchaseInvoiceId");
        builder.HasKey("PurchaseInvoiceId", nameof(PurchaseInvoiceLine.PurchaseOrderLineNumber));

        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.ComplexProperty(l => l.UnitPrice, m => m.MapMoney("UnitPrice"));

        builder.Ignore(l => l.LineTotal);
    }
}

public sealed class SupplierReturnConfiguration : IEntityTypeConfiguration<SupplierReturn>
{
    public void Configure(EntityTypeBuilder<SupplierReturn> builder)
    {
        builder.ToTable("SupplierReturns");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReturnNumber).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(r => r.Reason).HasConversion<int>();
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.CreditNoteNumber).HasMaxLength(60);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.BusinessDate).HasConversion(PurchasingMapping.BusinessDateConverter);

        // THE ONE PLACE MONEY IS NOT TWO TYPED COLUMNS, and it is not a preference.
        // CreditedAmount is Money? — null until the supplier issues a credit note, which
        // is a real and load-bearing distinction from "credited zero". EF Core 9 complex
        // properties cannot be nullable, and Money is a struct so it cannot be an
        // optional owned type either.
        //
        // The amount and currency are therefore encoded into one nullable column,
        // round-trip formatted so nothing is lost. It is queryable enough for its only
        // consumer — the credit-shortfall report computes in memory from the aggregate.
        // If it ever needs to be aggregated in SQL, the fix is to make the domain
        // property non-nullable with a companion HasCreditNote flag, not to widen this.
        builder.Property(r => r.CreditedAmount)
               .HasConversion(
                   money => money == null ? null : $"{money.Value.Amount:G29} {money.Value.Currency}",
                   text => text == null ? null : ParseMoney(text))
               .HasMaxLength(40)
               .HasColumnName("CreditedAmount");

        builder.HasMany(r => r.Lines)
               .WithOne()
               .HasForeignKey("SupplierReturnId")
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.ReturnNumber })
               .IsUnique()
               .HasDatabaseName("UX_SupplierReturns_Number");

        // Returns dispatched but never credited are money the merchant is owed and
        // nobody is chasing. This index is what makes that an answerable question.
        builder.HasIndex(r => new { r.TenantId, r.SupplierId, r.Status })
               .HasDatabaseName("IX_SupplierReturns_Outstanding");

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.ExpectedCredit);
        builder.Ignore(r => r.CreditShortfall);
    }

    private static Money ParseMoney(string text)
    {
        var separator = text.LastIndexOf(' ');

        return new Money(
            decimal.Parse(text[..separator], System.Globalization.CultureInfo.InvariantCulture),
            text[(separator + 1)..]);
    }
}

/// <inheritdoc cref="GoodsReceiptConfiguration"/>
public sealed class SupplierReturnLineConfiguration : IEntityTypeConfiguration<SupplierReturnLine>
{
    public void Configure(EntityTypeBuilder<SupplierReturnLine> builder)
    {
        builder.ToTable("SupplierReturnLines");

        builder.Property<Guid>("SupplierReturnId");
        builder.HasKey("SupplierReturnId", nameof(SupplierReturnLine.VariantId));

        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.ComplexProperty(l => l.UnitCost, m => m.MapMoney("UnitCost"));

        builder.Ignore(l => l.LineValue);
    }
}
