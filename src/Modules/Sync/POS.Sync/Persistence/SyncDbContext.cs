using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Sync.Domain;

namespace POS.Sync.Persistence;

public sealed class SyncDbContext(
    DbContextOptions<SyncDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "sync";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Sync";

    public DbSet<SyncBatch> Batches => Set<SyncBatch>();
    public DbSet<SyncedRecord> SyncedRecords => Set<SyncedRecord>();
    public DbSet<MasterDataVersion> MasterDataVersions => Set<MasterDataVersion>();
    public DbSet<TerminalSyncCursor> Cursors => Set<TerminalSyncCursor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SyncDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class SyncBatchConfiguration : IEntityTypeConfiguration<SyncBatch>
{
    public void Configure(EntityTypeBuilder<SyncBatch> builder)
    {
        builder.ToTable("Batches");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ProtocolVersion).HasMaxLength(20).IsRequired();
        builder.Property(e => e.FailureReason).HasMaxLength(2000);
        builder.Property(e => e.Status).HasConversion<int>();

        builder.HasIndex(e => new { e.TenantId, e.TerminalId, e.ReceivedAt });
        builder.HasIndex(e => new { e.Status, e.ReceivedAt });
    }
}

public sealed class SyncedRecordConfiguration : IEntityTypeConfiguration<SyncedRecord>
{
    public void Configure(EntityTypeBuilder<SyncedRecord> builder)
    {
        builder.ToTable("SyncedRecords");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.RecordType).HasMaxLength(200).IsRequired();

        // THE idempotency guarantee. Not an optimisation â€” the constraint IS the
        // correctness argument. An application-level "select then insert" races
        // under concurrency and duplicates under retry; the pre-check in
        // SyncIngestService only avoids doing work twice, and a unique-violation
        // on insert is handled as SUCCESS because it means somebody else won the
        // race with the same record.
        //
        // The failure this prevents is weekly, not hypothetical: terminal uploads,
        // server commits, response is lost, terminal retries. Without this, that is
        // a duplicated sale â€” real money, wrong stock, unbalanced drawer.
        builder.HasIndex(e => new { e.TerminalId, e.TerminalSequence })
               .IsUnique()
               .HasDatabaseName("UX_SyncedRecords_Terminal_Sequence");

        builder.HasIndex(e => e.BatchId);
        builder.HasIndex(e => e.RecordId);
    }
}

public sealed class MasterDataVersionConfiguration : IEntityTypeConfiguration<MasterDataVersion>
{
    public void Configure(EntityTypeBuilder<MasterDataVersion> builder)
    {
        builder.ToTable("MasterDataVersions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EntityType).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.EntityType }).IsUnique();
    }
}

public sealed class TerminalSyncCursorConfiguration : IEntityTypeConfiguration<TerminalSyncCursor>
{
    public void Configure(EntityTypeBuilder<TerminalSyncCursor> builder)
    {
        builder.ToTable("TerminalSyncCursors");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EntityType).HasMaxLength(200).IsRequired();

        // Advanced on ACKNOWLEDGE, never on send. If the cursor moved when the
        // server transmitted, a terminal that died mid-download would silently skip
        // master data and sell at last week's prices â€” a defect that surfaces as a
        // pricing dispute weeks later, not as an error.
        builder.HasIndex(e => new { e.TenantId, e.TerminalId, e.EntityType }).IsUnique();
    }
}
