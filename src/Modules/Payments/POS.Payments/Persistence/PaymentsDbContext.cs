using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Payments.Domain;

namespace POS.Payments.Persistence;

/// <summary>The Payments module's persistence boundary.</summary>
/// <remarks>
/// This context exists to serve one ordering rule that the whole payments design rests
/// on (ADR 042): the payment record is written and COMMITTED before the provider is
/// called. <see cref="PaymentsDbContext"/> is therefore used through
/// <see cref="EfPaymentStore"/>, which commits explicitly at each step rather than
/// deferring to a request-scoped unit of work. A payment that is only in the change
/// tracker when the process dies is money moved with no local evidence of it.
/// </remarks>
public sealed class PaymentsDbContext(
    DbContextOptions<PaymentsDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "payments";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Payments";

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
        TenantQueryFilter.ApplyTo(modelBuilder, this);
        base.OnModelCreating(modelBuilder);
    }
}
