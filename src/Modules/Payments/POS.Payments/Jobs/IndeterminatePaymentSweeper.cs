using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using POS.Common.Jobs;
using POS.Common.Tenancy;
using POS.Payments.Domain;
using POS.Payments.Orchestration;
using POS.Payments.Persistence;

namespace POS.Payments.Jobs;

/// <summary>
/// Chases payments whose outcome was never learned to a definite conclusion.
/// </summary>
/// <remarks>
/// <para>
/// A payment goes <see cref="PaymentStatus.Indeterminate"/> when the provider was
/// called but the response was lost — a dropped connection at the worst possible moment.
/// It is the one state the system may never guess at: assume success and a decline gives
/// goods away; assume failure and the retry double-charges a customer whose card already
/// worked (ADR 044). The only safe resolution is to ASK THE PROVIDER what happened, which
/// is what <see cref="PaymentOrchestrator.ResolveAsync"/> does. This sweep is what makes
/// sure something asks, rather than leaving the payment stuck forever waiting for a
/// request that never comes back.
/// </para>
/// <para>
/// It runs as a SYSTEM OPERATION across every tenant. The cross-tenant scan bypasses the
/// query filter deliberately — there is no request tenant to filter by — and then each
/// payment is resolved under its OWN tenant, so the write that records the outcome is
/// stamped and guarded correctly. Bypassing to read and re-entering to write is the
/// established shape for background work (see <see cref="ITenantContext"/>).
/// </para>
/// <para>
/// The work lives here, in a plain scoped service, so a test can drive one sweep
/// directly instead of waiting on a timer.
/// </para>
/// </remarks>
public sealed class IndeterminatePaymentSweeper(
    PaymentsDbContext db,
    PaymentOrchestrator orchestrator,
    ITenantContext tenantContext,
    ILogger<IndeterminatePaymentSweeper> logger)
{
    /// <summary>
    /// How many payments one sweep resolves at most.
    /// </summary>
    /// <remarks>
    /// Bounded so a backlog is drained over several ticks rather than in one long
    /// transaction-heavy pass that holds the provider and the database busy. Each
    /// resolution is its own commit, so a partial sweep loses nothing.
    /// </remarks>
    public const int BatchSize = 100;

    /// <summary>Resolves up to <see cref="BatchSize"/> indeterminate payments.</summary>
    /// <returns>How many were attempted.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        // Cross-tenant read: IgnoreQueryFilters means the tenant filter — which would
        // otherwise throw here, there being no resolved tenant — is not applied. The
        // rows carry their own TenantId, which is all the per-payment step needs.
        var due = await db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Indeterminate)
            .OrderBy(p => p.InitiatedAt)
            .Take(BatchSize)
            .Select(p => new { p.Id, p.TenantId })
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return 0;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Indeterminate payment sweep found {Count} payment(s) to resolve.", due.Count);

        var attempted = 0;

        foreach (var payment in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Act AS that tenant for this one resolution. The scope restores the previous
            // (empty) tenant when it disposes, so the next payment starts clean.
            using var _ = tenantContext.EnterTenant(payment.TenantId, "indeterminate payment sweep");

            // The orchestrator owns the actual resolution: it asks the provider, and only
            // a definite "the provider never saw it" answer is allowed to mark the
            // payment failed. Anything less leaves it indeterminate for the next sweep.
            var result = await orchestrator.ResolveAsync(payment.TenantId, payment.Id, cancellationToken);

            attempted++;

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "Could not resolve indeterminate payment {PaymentId} for tenant {TenantId}: {Code} {Message}",
                    payment.Id,
                    payment.TenantId,
                    result.Error.Code,
                    result.Error.Message);
            }

            // The DbContext is shared across the loop; clearing keeps one payment's
            // tracked graph from colliding with the next resolution's load.
            db.ChangeTracker.Clear();
        }

        return attempted;
    }
}

/// <summary>Runs the indeterminate-payment sweep on an interval.</summary>
public sealed class IndeterminatePaymentSweepJob(
    IServiceScopeFactory scopeFactory,
    PaymentsJobOptions options,
    ILogger<IndeterminatePaymentSweepJob> logger)
    : PeriodicJob<IndeterminatePaymentSweeper>(scopeFactory, logger)
{
    protected override TimeSpan Interval => options.IndeterminateSweepInterval;

    protected override string JobName => "indeterminate-payment-sweep";

    protected override Task RunAsync(IndeterminatePaymentSweeper worker, CancellationToken cancellationToken) =>
        worker.SweepAsync(cancellationToken);
}

/// <summary>Timings for the Payments module's background jobs.</summary>
/// <remarks>
/// Configuration, not tenant data: how often the estate sweeps for stuck payments is an
/// operations decision. Five minutes is a deliberate default — an indeterminate payment
/// is money in limbo, and a customer waiting to know whether they were charged should
/// not wait an hour.
/// </remarks>
public sealed class PaymentsJobOptions
{
    public const string SectionName = "Payments:Jobs";

    public TimeSpan IndeterminateSweepInterval { get; init; } = TimeSpan.FromMinutes(5);
}
