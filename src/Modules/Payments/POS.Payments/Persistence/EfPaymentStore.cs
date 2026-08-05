using Microsoft.EntityFrameworkCore;
using POS.Payments.Abstractions;
using POS.Payments.Domain;

namespace POS.Payments.Persistence;

/// <summary>The EF Core implementation of the payment store.</summary>
/// <remarks>
/// EVERY METHOD COMMITS, and the interface says so in its names. That is unusual —
/// most of this codebase treats DbContext as the unit of work and lets the request
/// decide when to save (ADR 009) — and it is deliberate here.
///
/// <see cref="Orchestration.PaymentOrchestrator"/> writes the payment record and
/// commits it BEFORE calling the provider. If the commit were deferred to the end of
/// the request, a crash during authorisation would leave money moved at the acquirer
/// and no local evidence of it: a state no query can detect, which surfaces days later
/// as an unexplained line in a settlement file (ADR 042).
///
/// The reads are tracked, not <c>AsNoTracking</c>, because every caller of
/// <see cref="FindByIdAsync"/> is about to mutate the payment and save it.
/// </remarks>
public sealed class EfPaymentStore(PaymentsDbContext db) : IPaymentStore
{
    public async Task AddAndCommitAsync(Payment payment, CancellationToken cancellationToken)
    {
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAndCommitAsync(Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        // Attach only if the instance is not already tracked. The orchestrator's own
        // flow reads then updates, so the common case is already tracked and calling
        // Update() on it would mark every property modified for no reason.
        if (db.Entry(payment).State == EntityState.Detached)
            db.Payments.Update(payment);

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Payment?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken) =>
        db.Payments
          .Include(p => p.Attempts)
          .FirstOrDefaultAsync(
              p => p.TenantId == tenantId && p.IdempotencyKey == idempotencyKey,
              cancellationToken);

    public Task<Payment?> FindByIdAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken) =>
        db.Payments
          .Include(p => p.Attempts)
          .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == paymentId, cancellationToken);
}
