using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Expenses.Domain;
using POS.SharedKernel;

namespace POS.Expenses.Persistence;

/// <summary>The Expenses module's persistence boundary.</summary>
public sealed class ExpensesDbContext(
    DbContextOptions<ExpensesDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "expenses";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Expenses";

    public DbSet<Expense> Expenses => Set<Expense>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExpensesDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.ToTable("Expenses");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ExpenseNumber).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(500).IsRequired();
        builder.Property(e => e.RejectionReason).HasMaxLength(500);
        builder.Property(e => e.Category).HasConversion<int>();
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.ComplexProperty(e => e.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasPrecision(19, 4);
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3).IsFixedLength();
        });

        builder.ComplexProperty(e => e.TaxAmount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("TaxAmount").HasPrecision(19, 4);
            m.Property(x => x.Currency).HasColumnName("TaxCurrency").HasMaxLength(3).IsFixedLength();
        });

        builder.HasIndex(e => new { e.TenantId, e.ExpenseNumber })
               .IsUnique()
               .HasDatabaseName("UX_Expenses_Number");

        // The approval queue, and the separation-of-duties audit behind it.
        builder.HasIndex(e => new { e.TenantId, e.Status, e.IncurredOn })
               .HasDatabaseName("IX_Expenses_Approval");

        // "What did this branch spend on utilities last quarter?" — the only reporting
        // question this module exists to answer, and it should not be a table scan.
        builder.HasIndex(e => new { e.TenantId, e.CompanyId, e.BranchId, e.Category, e.IncurredOn })
               .HasDatabaseName("IX_Expenses_Reporting");

        // Capitalised expenses are those linked to a goods receipt: the freight on a
        // delivery becomes part of stock value rather than a period cost (ADR 053).
        // Finding them is a period-close activity, so it is indexed and filtered rather
        // than scanning every expense ever recorded.
        builder.HasIndex(e => e.LinkedGoodsReceiptId)
               .HasFilter("[LinkedGoodsReceiptId] IS NOT NULL")
               .HasDatabaseName("IX_Expenses_Capitalised");

        // Computed from Amount + TaxAmount, and from the goods-receipt link. Deriving
        // them in SQL would let the two disagree.
        builder.Ignore(e => e.GrossAmount);
        builder.Ignore(e => e.IsCapitalised);
        builder.Ignore(e => e.DomainEvents);
    }
}
