using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.Payments.Persistence;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Kind).HasConversion<int>();
        builder.Property(p => p.Status).HasConversion<int>();
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.ProviderCode).HasMaxLength(50).IsRequired();
        builder.Property(p => p.ProviderReference).HasMaxLength(100);
        builder.Property(p => p.AuthorisationCode).HasMaxLength(50);
        builder.Property(p => p.FailureCode).HasMaxLength(50);
        builder.Property(p => p.FailureMessage).HasMaxLength(500);

        // A value object over a string, converted rather than owned: it is one column,
        // it is the target of the idempotency lookup on every payment attempt, and an
        // owned type would put it in a side table that has to be joined on the hottest
        // path in the module.
        builder.Property(p => p.IdempotencyKey)
               .HasConversion(key => key.Value, value => new IdempotencyKey(value))
               .HasMaxLength(100)
               .IsRequired();

        builder.Property(p => p.BusinessDate)
               .HasConversion(date => date.Value, value => BusinessDate.Open(value));

        builder.ComplexProperty(p => p.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasPrecision(19, 4);
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3).IsFixedLength();
        });

        builder.ComplexProperty(p => p.CapturedAmount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("CapturedAmount").HasPrecision(19, 4);
            m.Property(x => x.Currency).HasColumnName("CapturedCurrency").HasMaxLength(3).IsFixedLength();
        });

        builder.ComplexProperty(p => p.RefundedAmount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("RefundedAmount").HasPrecision(19, 4);
            m.Property(x => x.Currency).HasColumnName("RefundedCurrency").HasMaxLength(3).IsFixedLength();
        });

        // NO CARDHOLDER DATA. MaskedPan is the last four digits as presented by the
        // terminal, Token is the provider's own reference. A PAN, track data or CVV
        // must never reach this table — P2PE means the data is encrypted before it
        // leaves the pin pad and this system never holds the key (ADR 043).
        builder.OwnsOne(p => p.Instrument, i =>
        {
            i.Property(x => x.MaskedPan).HasColumnName("InstrumentMaskedPan").HasMaxLength(24);
            i.Property(x => x.Scheme).HasColumnName("InstrumentScheme").HasMaxLength(30);
            i.Property(x => x.EntryMode).HasColumnName("InstrumentEntryMode").HasConversion<int>();
            i.Property(x => x.Token).HasColumnName("InstrumentToken").HasMaxLength(200);
        });

        // Attempts carry no Money, so they can be owned — unlike the Sales module's
        // price adjustments, which cannot.
        builder.OwnsMany(p => p.Attempts, a =>
        {
            a.ToTable("PaymentAttempts");
            a.WithOwner().HasForeignKey("PaymentId");
            a.HasKey("PaymentId", nameof(PaymentAttempt.AttemptNumber));

            // AttemptNumber is ASSIGNED BY THE AGGREGATE — it is 1, 2, 3 in the order
            // attempts happened, not a database counter. EF's convention would make an
            // int key column an IDENTITY, and then the explicit value the domain sets
            // collides with it ("cannot insert explicit value for identity column") the
            // first time an attempt is written. ValueGeneratedNever hands the numbering
            // back to the domain, which is where it belongs.
            a.Property(x => x.AttemptNumber).ValueGeneratedNever();

            a.Property(x => x.Outcome).HasMaxLength(50).IsRequired();
            a.Property(x => x.Detail).HasMaxLength(1000);
        });

        builder.Navigation(p => p.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);

        // THE idempotency lookup, and the constraint that makes it a guarantee rather
        // than a hope. Without uniqueness, two racing retries of the same intent both
        // miss the pre-check and the customer is charged twice.
        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey })
               .IsUnique()
               .HasDatabaseName("UX_Payments_Idempotency");

        // The indeterminate-resolution sweep (ADR 044). Payments whose outcome is
        // unknown must be actively chased, not waited on, so this must not be a scan.
        builder.HasIndex(p => new { p.TenantId, p.Status, p.InitiatedAt })
               .HasDatabaseName("IX_Payments_Status");

        // Settlement reconciliation, and "what did this sale actually take?".
        builder.HasIndex(p => new { p.TenantId, p.SaleId })
               .HasDatabaseName("IX_Payments_Sale");

        builder.HasIndex(p => new { p.TenantId, p.ProviderCode, p.ProviderReference })
               .HasDatabaseName("IX_Payments_ProviderReference");

        builder.Ignore(p => p.DomainEvents);
        builder.Ignore(p => p.IsFinal);
        builder.Ignore(p => p.RefundableAmount);
    }
}
