using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Fiscal.Domain;
using POS.SharedKernel;

namespace POS.Fiscal.Persistence;

/// <summary>The Fiscal module's persistence boundary.</summary>
/// <remarks>
/// Fiscal documents are IMMUTABLE once issued. A correction is a new document that
/// supersedes the old one (ADR 006), which is why there is no soft-delete column here
/// and no update path beyond transmission status. Editing an issued fiscal document is
/// a compliance offence in most regimes, so the model does not offer the option.
/// </remarks>
public sealed class FiscalDbContext(
    DbContextOptions<FiscalDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "fiscal";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Fiscal";

    public DbSet<FiscalDocument> Documents => Set<FiscalDocument>();

    /// <summary>Per (company, terminal, series) counter backing the gap-free allocator.</summary>
    public DbSet<FiscalSequence> Sequences => Set<FiscalSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FiscalDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<FiscalDocument>
{
    public void Configure(EntityTypeBuilder<FiscalDocument> builder)
    {
        builder.ToTable("FiscalDocuments");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.ProfileCode).HasMaxLength(30).IsRequired();
        builder.Property(d => d.Series).HasMaxLength(20).IsRequired();
        builder.Property(d => d.FormattedNumber).HasMaxLength(60).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Status).HasConversion<int>();

        // The document as issued, byte for byte. Stored rather than regenerated
        // because a regenerated document is a different document: the signature and
        // the hash chain are over these exact bytes.
        builder.Property(d => d.Content).IsRequired();

        builder.Property(d => d.CanonicalHash).HasMaxLength(128).IsRequired();
        builder.Property(d => d.PreviousDocumentHash).HasMaxLength(128);
        builder.Property(d => d.SignatureAlgorithm).HasMaxLength(50);
        builder.Property(d => d.SignatureValue).HasMaxLength(2000);
        builder.Property(d => d.CertificateThumbprint).HasMaxLength(100);
        builder.Property(d => d.AuthorityIdentifier).HasMaxLength(200);
        builder.Property(d => d.QrPayload).HasMaxLength(2000);

        builder.OwnsMany(d => d.Attempts, a =>
        {
            a.ToTable("FiscalTransmissionAttempts");
            a.WithOwner().HasForeignKey("FiscalDocumentId");
            a.HasKey(x => x.Id);
            a.Property(x => x.Outcome).HasMaxLength(50).IsRequired();
            a.Property(x => x.AuthorityIdentifier).HasMaxLength(200);
            a.Property(x => x.MessageCode).HasMaxLength(50);
            a.Property(x => x.MessageText).HasMaxLength(2000);
        });

        builder.Navigation(d => d.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Gap-free numbering per (company, terminal, series). Same reasoning as the
        // sales receipt: the allocator is the mechanism, this is the guarantee. A gap
        // is a compliance finding in most regimes, and so is a duplicate.
        builder.HasIndex(d => new { d.CompanyId, d.TerminalId, d.Series, d.Sequence })
               .IsUnique()
               .HasDatabaseName("UX_FiscalDocuments_Number");

        // The hash-chain lookup: "what was the last document in this series?" It runs
        // on every issuance in a chaining jurisdiction, so it is on the sale path.
        builder.HasIndex(d => new { d.CompanyId, d.TerminalId, d.Series, d.IssuedAt })
               .HasDatabaseName("IX_FiscalDocuments_Chain");

        // The transmission queue and the overdue alarm. Both filter on status and a
        // date, and the overdue one is an operational alarm that must stay cheap.
        builder.HasIndex(d => new { d.Status, d.TransmissionDueBy })
               .HasDatabaseName("IX_FiscalDocuments_Transmission");

        builder.HasIndex(d => new { d.TenantId, d.SaleId })
               .HasDatabaseName("IX_FiscalDocuments_Sale");

        builder.Ignore(d => d.DomainEvents);
    }
}

/// <summary>
/// The persistent counter behind the gap-free fiscal numbering strategy.
/// </summary>
/// <remarks>
/// A ROW PER SERIES, incremented under a database lock. Not an IDENTITY column and not
/// a sequence object: both allocate on insert and both leak numbers when a transaction
/// rolls back. A gap-free series cannot tolerate that — a burnt number is a compliance
/// finding, which is also why the fiscalisation pipeline checks offline legality
/// BEFORE it allocates.
/// </remarks>
public sealed class FiscalSequence : ITenantScoped
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid TerminalId { get; private set; }
    public string Series { get; private set; } = null!;
    public long LastAllocated { get; private set; }

    public static FiscalSequence Start(Guid companyId, Guid terminalId, string series) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = companyId,
        TerminalId = terminalId,
        Series = series,
        LastAllocated = 0
    };

    public long Next() => ++LastAllocated;
}

public sealed class FiscalSequenceConfiguration : IEntityTypeConfiguration<FiscalSequence>
{
    public void Configure(EntityTypeBuilder<FiscalSequence> builder)
    {
        builder.ToTable("FiscalSequences");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Series).HasMaxLength(20).IsRequired();

        builder.HasIndex(s => new { s.CompanyId, s.TerminalId, s.Series })
               .IsUnique()
               .HasDatabaseName("UX_FiscalSequences_Series");
    }
}
