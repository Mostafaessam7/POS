using Microsoft.EntityFrameworkCore;
using POS.Fiscal.Domain;
using POS.Fiscal.Jobs;
using POS.Fiscal.Persistence;
using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.Payments.Jobs;
using POS.Payments.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The maintenance jobs, driven directly rather than on their timers.
/// </summary>
/// <remarks>
/// A background job that has only ever been observed to compile is not tested. Each
/// worker here is resolved from the host and run once against real seeded state, which
/// is exactly what its timer would do — minus the wait.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class BackgroundJobTests(ApiFixture fixture)
{
    /// <summary>
    /// The sweep resolves a payment whose provider turns out never to have seen it.
    /// </summary>
    /// <remarks>
    /// A <see cref="PaymentOutcomeStatus.NotFound"/> answer is the one definite negative:
    /// the provider has no record, so no money moved, so the payment may safely be
    /// failed (ADR 044). This is the full mechanism — cross-tenant scan, per-tenant
    /// resolution, provider query — ending in a definite state.
    /// </remarks>
    [Fact]
    public async Task Sweep_fails_an_indeterminate_payment_the_provider_never_saw()
    {
        TestPaymentProvider.NextQueryStatus = PaymentOutcomeStatus.NotFound;

        var tenant = await fixture.CreateTenantAsync();
        var paymentId = await fixture.SeedIndeterminatePaymentAsync(tenant);

        await fixture.RunWorkerAsync<IndeterminatePaymentSweeper, int>(w => w.SweepAsync());

        var status = await fixture.ReadAsync<PaymentsDbContext, PaymentStatus>(tenant, db =>
            db.Payments.Where(p => p.Id == paymentId).Select(p => p.Status).FirstAsync());

        status.ShouldBe(PaymentStatus.Failed);
    }

    /// <summary>
    /// An <see cref="PaymentOutcomeStatus.Unknown"/> answer leaves the payment untouched.
    /// </summary>
    /// <remarks>
    /// This is the case the whole indeterminate design exists to protect: when the
    /// provider still cannot say, the system must NOT guess. Marking it paid gives away
    /// goods; marking it failed double-charges. It stays indeterminate for the next
    /// sweep, and that non-action is the correct action.
    /// </remarks>
    [Fact]
    public async Task Sweep_leaves_a_payment_indeterminate_when_the_provider_still_cannot_say()
    {
        TestPaymentProvider.NextQueryStatus = PaymentOutcomeStatus.Unknown;

        var tenant = await fixture.CreateTenantAsync();
        var paymentId = await fixture.SeedIndeterminatePaymentAsync(tenant);

        await fixture.RunWorkerAsync<IndeterminatePaymentSweeper, int>(w => w.SweepAsync());

        var status = await fixture.ReadAsync<PaymentsDbContext, PaymentStatus>(tenant, db =>
            db.Payments.Where(p => p.Id == paymentId).Select(p => p.Status).FirstAsync());

        status.ShouldBe(PaymentStatus.Indeterminate);

        // Restore the default so a later test's payment is not accidentally left unknown.
        TestPaymentProvider.NextQueryStatus = PaymentOutcomeStatus.NotFound;
    }

    /// <summary>One sweep reaches payments in more than one tenant.</summary>
    /// <remarks>
    /// The sweep runs as a system operation with no request tenant; it finds
    /// indeterminate payments across every tenant and resolves each under its own. Two
    /// tenants, one sweep, both resolved is the proof that the cross-tenant scan and the
    /// per-tenant re-entry both work.
    /// </remarks>
    [Fact]
    public async Task Sweep_reaches_payments_across_tenants()
    {
        TestPaymentProvider.NextQueryStatus = PaymentOutcomeStatus.NotFound;

        var tenantA = await fixture.CreateTenantAsync();
        var tenantB = await fixture.CreateTenantAsync();

        var paymentA = await fixture.SeedIndeterminatePaymentAsync(tenantA);
        var paymentB = await fixture.SeedIndeterminatePaymentAsync(tenantB);

        await fixture.RunWorkerAsync<IndeterminatePaymentSweeper, int>(w => w.SweepAsync());

        var statusA = await fixture.ReadAsync<PaymentsDbContext, PaymentStatus>(tenantA, db =>
            db.Payments.Where(p => p.Id == paymentA).Select(p => p.Status).FirstAsync());
        var statusB = await fixture.ReadAsync<PaymentsDbContext, PaymentStatus>(tenantB, db =>
            db.Payments.Where(p => p.Id == paymentB).Select(p => p.Status).FirstAsync());

        statusA.ShouldBe(PaymentStatus.Failed);
        statusB.ShouldBe(PaymentStatus.Failed);
    }

    /// <summary>A fiscal document past its transmission deadline is reported.</summary>
    [Fact]
    public async Task Monitor_reports_an_overdue_fiscal_document()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 1));

        // The GENERIC profile sets no deadline, so give this one a past one to make it
        // overdue. Fiscal documents have no in-place immutability guard on transmission
        // status, so this is a legitimate mutation through the aggregate.
        Guid documentId = await fixture.ReadAsync<FiscalDbContext, Guid>(tenant, db =>
            db.Documents.Select(d => d.Id).FirstAsync());

        await fixture.WriteAsync<FiscalDbContext>(tenant, async db =>
        {
            var document = await db.Documents.FirstAsync(d => d.Id == documentId);
            document.SetTransmissionDeadline(DateTimeOffset.UtcNow.AddDays(-1));
        });

        var overdue = await fixture.RunWorkerAsync<FiscalDeadlineMonitor, IReadOnlyList<Guid>>(
            w => w.CheckAsync());

        overdue.ShouldContain(documentId);
    }

    /// <summary>
    /// A document with no deadline is not reported overdue.
    /// </summary>
    /// <remarks>
    /// The GENERIC baseline: a jurisdiction with no clearance mandate issues terminally,
    /// so its documents never acquire a deadline and the monitor correctly leaves them
    /// alone. Asserted against this document specifically, because the monitor is
    /// cross-tenant and other tests deliberately create overdue documents.
    /// </remarks>
    [Fact]
    public async Task Monitor_ignores_a_document_with_no_deadline()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 1));

        var documentId = await fixture.ReadAsync<FiscalDbContext, Guid>(tenant, db =>
            db.Documents.Select(d => d.Id).FirstAsync());

        var overdue = await fixture.RunWorkerAsync<FiscalDeadlineMonitor, IReadOnlyList<Guid>>(
            w => w.CheckAsync());

        overdue.ShouldNotContain(documentId);
    }
}
