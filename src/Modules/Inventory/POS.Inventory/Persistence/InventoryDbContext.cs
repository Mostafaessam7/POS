using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Inventory.Domain;
using POS.SharedKernel;

namespace POS.Inventory.Persistence;

public sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{

    public const string Schema = "inventory";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Inventory";

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<Stocktake> Stocktakes => Set<Stocktake>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);

        // SECURITY: this line was missing. Inventory was the only module that never
        // applied the filter, leaving stock movements and balances readable across
        // tenants. Catalog, Identity and Sync all had it — which is precisely the
        // failure mode TenantQueryFilter's own remarks warn about: centralising the
        // MECHANISM does not help if APPLYING it stays manual and per-context.
        // ArchitectureTests now fails the build if any DbContext omits this.
        TenantQueryFilter.ApplyTo(modelBuilder, this);

        // Per-module migration history (ADR 001): Inventory versions independently of
        // Catalog and Sales, so a schema change here does not block their deployments.
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Enforces ledger immutability at the last possible moment.
    /// </summary>
    /// <remarks>
    /// ADR 008 says movements are append-only. Saying it in a document stops nobody; a
    /// well-meaning bug-fix that "corrects" a movement in place would silently destroy
    /// the audit trail and pass code review. This check makes the rule executable — the
    /// only kind of rule that survives three years and a change of team.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardLedgerImmutability();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc cref="SaveChanges(bool)"/>
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardLedgerImmutability();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardLedgerImmutability()
    {
        var violations = ChangeTracker
            .Entries<StockMovement>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Attempted to {violations[0].State.ToString().ToLowerInvariant()} {violations.Count} " +
                "stock movement(s). The stock ledger is append-only (ADR 008); correct a " +
                "mistake with a compensating movement instead.");
        }
    }
}

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.QuantityDelta).HasPrecision(18, 4);

        // Quantities are decimal, not int: weighed goods sell in fractional kilograms
        // and a 0.325kg sale must be representable exactly.

        builder.ComplexProperty(m => m.UnitCost, b =>
        {
            // Precision 6 rather than 2 — a weighted average unit cost must retain
            // sub-penny precision or it drifts when multiplied back up (ADR 020).
            b.Property(p => p.Amount).HasColumnName("UnitCostAmount").HasPrecision(19, 6);
            b.Property(p => p.Currency).HasColumnName("UnitCostCurrency").HasMaxLength(3);
        });

        builder.ComplexProperty(m => m.TotalCost, b =>
        {
            b.Property(p => p.Amount).HasColumnName("TotalCostAmount").HasPrecision(19, 6);
            b.Property(p => p.Currency).HasColumnName("TotalCostCurrency").HasMaxLength(3);
        });

        builder.ComplexProperty(m => m.Reference, b =>
        {
            b.Property(p => p.DocumentType).HasColumnName("DocumentType");
            b.Property(p => p.DocumentId).HasColumnName("DocumentId");
            b.Property(p => p.DocumentNumber).HasColumnName("DocumentNumber").HasMaxLength(50);
        });

        builder.Property(m => m.ReasonCode).HasMaxLength(50);
        builder.Property(m => m.Notes).HasMaxLength(500);

        // The reconciliation and balance-rebuild query: every movement for one variant
        // in one warehouse, in order. This is THE index of the module.
        builder.HasIndex(m => new { m.TenantId, m.WarehouseId, m.VariantId, m.OccurredAt })
               .HasDatabaseName("IX_StockMovements_Balance");

        // Daily stock reporting and period-close.
        builder.HasIndex(m => new { m.TenantId, m.BusinessDate })
               .HasDatabaseName("IX_StockMovements_BusinessDate");

        // "Why did this sale not reduce stock?" — the most common support query.
        //
        // IX_StockMovements_Document IS NOT DECLARED HERE, and cannot be. EF Core 9
        // rejects an index over a complex-type member: HasIndex only accepts a simple
        // property access, and Reference.DocumentId is one level down. The column
        // exists — complex properties flatten onto this table — so the index is
        // created as explicit SQL in the initial migration instead, alongside this
        // comment's counterpart.
        //
        // Consequence to know about: the index is invisible to the model snapshot, so
        // EF will neither drop nor recreate it. If DocumentId's column name changes,
        // the migration that renames it must carry the index change by hand.
        //
        // The alternatives were worse. Demoting Reference to an owned entity type
        // would let it carry an index but not one that also spans TenantId, which is
        // the leading column that makes this index selective. Duplicating DocumentId
        // as a second scalar would put a denormalised field into the domain model to
        // work around a mapping limitation.

        // NO soft delete and NO IsDeleted column. Append-only means append-only; a
        // deletable ledger row is a contradiction (ADR 008, and the transactional-record
        // row of the Phase 1 soft-delete table).
    }
}

public sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("StockBalances");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.QuantityOnHand).HasPrecision(18, 4);

        builder.ComplexProperty(b => b.AverageUnitCost, x =>
        {
            x.Property(p => p.Amount).HasColumnName("AverageUnitCostAmount").HasPrecision(19, 6);
            x.Property(p => p.Currency).HasColumnName("AverageUnitCostCurrency").HasMaxLength(3);
        });

        builder.ComplexProperty(b => b.TotalValue, x =>
        {
            x.Property(p => p.Amount).HasColumnName("TotalValueAmount").HasPrecision(19, 6);
            x.Property(p => p.Currency).HasColumnName("TotalValueCurrency").HasMaxLength(3);
        });

        builder.Property(b => b.RowVersion).IsRowVersion();

        // The uniqueness that makes the relative-UPDATE fast path safe: exactly one
        // balance row per variant per warehouse, so the UPDATE can never touch two rows
        // and a concurrent insert loses cleanly against the constraint.
        builder.HasIndex(b => new { b.TenantId, b.WarehouseId, b.VariantId })
               .IsUnique()
               .HasDatabaseName("UX_StockBalances_Warehouse_Variant");

        // Negative balances are an exception report, not an error (ADR 027). A filtered
        // index keeps that report cheap without indexing the whole table.
        builder.HasIndex(b => new { b.TenantId, b.WarehouseId })
               .HasFilter("[QuantityOnHand] < 0")
               .HasDatabaseName("IX_StockBalances_Negative");
    }
}

public sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        builder.ToTable("StockTransfers");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TransferNumber).HasMaxLength(30).IsRequired();
        builder.Property(t => t.RowVersion).IsRowVersion();
        builder.Property(t => t.VarianceWriteOffReason).HasMaxLength(200);

        builder.HasIndex(t => new { t.TenantId, t.TransferNumber })
               .IsUnique()
               .HasDatabaseName("UX_StockTransfers_Number");

        // Finding transfers stuck in transit is an operational necessity — stock sitting
        // in transit for weeks is either lost or stolen, and nobody notices without this.
        builder.HasIndex(t => new { t.TenantId, t.Status, t.DispatchedAt })
               .HasDatabaseName("IX_StockTransfers_Status");

        builder.HasMany(t => t.Lines)
               .WithOne()
               .HasForeignKey(l => l.StockTransferId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.DomainEvents);
        builder.Ignore(t => t.HasVariance);
    }
}

public sealed class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> builder)
    {
        builder.ToTable("StockTransferLines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.QuantitySent).HasPrecision(18, 4);
        builder.Property(l => l.QuantityReceived).HasPrecision(18, 4);

        builder.Ignore(l => l.Variance);
        builder.Ignore(l => l.HasVariance);
    }
}

public sealed class StocktakeConfiguration : IEntityTypeConfiguration<Stocktake>
{
    public void Configure(EntityTypeBuilder<Stocktake> builder)
    {
        builder.ToTable("Stocktakes");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.StocktakeNumber).HasMaxLength(30).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.HasIndex(s => new { s.TenantId, s.StocktakeNumber })
               .IsUnique()
               .HasDatabaseName("UX_Stocktakes_Number");

        builder.HasIndex(s => new { s.TenantId, s.WarehouseId, s.Status })
               .HasDatabaseName("IX_Stocktakes_Warehouse_Status");

        builder.HasMany(s => s.Lines)
               .WithOne()
               .HasForeignKey(l => l.StocktakeId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(s => s.DomainEvents);
    }
}

public sealed class StocktakeLineConfiguration : IEntityTypeConfiguration<StocktakeLine>
{
    public void Configure(EntityTypeBuilder<StocktakeLine> builder)
    {
        builder.ToTable("StocktakeLines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.CountedQuantity).HasPrecision(18, 4);
        builder.Property(l => l.ExpectedQuantity).HasPrecision(18, 4);

        builder.Ignore(l => l.Variance);
    }
}
