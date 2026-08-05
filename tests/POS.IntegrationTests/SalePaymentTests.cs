using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Identity.Authorization;
using POS.Payments.Domain;
using POS.Payments.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Synced sales record their electronic tenders as payments.
/// </summary>
/// <remarks>
/// The Payments module was fully implemented, migrated, and had never written a
/// payment: an orchestrator, a store, a provider seam and a settlement reconciler with
/// nothing to reconcile. The sync path now records each electronic tender as a captured
/// offline payment, which is what these tests exercise — and, with that, the Sale ↔
/// Payment reconciliation finally has both sides.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class SalePaymentTests(ApiFixture fixture)
{
    [Fact]
    public async Task A_card_tender_is_recorded_as_a_captured_payment()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 3);

        var response = await fixture.UploadAsync(tenant, batch);
        response.Accepted.ShouldBe(3);

        var payments = await fixture.ReadAsync<PaymentsDbContext, List<Payment>>(tenant, db =>
            db.Payments.AsNoTracking().ToListAsync());

        // One card tender per generated sale.
        payments.Count.ShouldBe(3);

        // Captured, not merely initiated: the money was taken at the till, and recording
        // it as still-owing would misstate the settlement position.
        payments.ShouldAllBe(p => p.Status == PaymentStatus.Captured);
        payments.ShouldAllBe(p => p.AuthorisedOffline);
        payments.ShouldAllBe(p => p.Amount.Amount == 10.00m);
        payments.ShouldAllBe(p => p.CapturedAmount.Amount == 10.00m);

        // The provider is recorded as the tender method, because the acquirer is not
        // known from an offline capture.
        payments.ShouldAllBe(p => p.ProviderCode == "TERMINAL_CARD");
    }

    /// <summary>
    /// A cash tender records no payment.
    /// </summary>
    /// <remarks>
    /// Cash is drawer accountability, reconciled by the shift, and has none of the
    /// auth/capture/settle lifecycle the Payments module owns. Recording it here would
    /// force a payment with no provider and no settlement path.
    /// </remarks>
    [Fact]
    public async Task A_cash_only_sale_records_no_payment()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();

        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 1);
        var cashOnly = batch with
        {
            Records =
            [
                ApiFixture.WithCashTender(batch.Records[0])
            ]
        };

        var response = await fixture.UploadAsync(tenant, cashOnly);
        response.Accepted.ShouldBe(1);

        var count = await fixture.ReadAsync<PaymentsDbContext, int>(tenant, db =>
            db.Payments.CountAsync());

        count.ShouldBe(0);
    }

    /// <summary>Replaying a batch records each tender once.</summary>
    /// <remarks>
    /// A duplicated card payment doubles what the settlement reconciliation expects to
    /// see arrive from the acquirer, so the recording adapter is idempotent per tender —
    /// keyed on the sale and the tender's position, both of which a replay reproduces.
    /// </remarks>
    [Fact]
    public async Task Replaying_a_batch_records_each_tender_once()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 4);

        await fixture.UploadAsync(tenant, batch);
        var replay = await fixture.UploadAsync(tenant, batch);

        replay.Accepted.ShouldBe(0);
        replay.Duplicates.ShouldBe(4);

        var count = await fixture.ReadAsync<PaymentsDbContext, int>(tenant, db =>
            db.Payments.CountAsync());

        count.ShouldBe(4);
    }

    [Fact]
    public async Task Sale_payment_reconciliation_is_clean()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 3));

        var (auditor, _) = await fixture.CreateClientWithPermissionsAsync(
            tenant, Guid.Empty, Permissions.Reports.ReconciliationView);

        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/sale-payment-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeTrue();
    }

    /// <summary>A sale whose payment never recorded is reported, at the amount owed.</summary>
    [Fact]
    public async Task A_sale_missing_its_payment_is_reported()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 2));

        // Simulates the crash window between the sale save and the tender recording.
        var deleted = await fixture.ReadAsync<PaymentsDbContext, int>(tenant, db =>
            db.Payments
              .Where(p => p.ProviderReference != null)
              .OrderBy(p => p.InitiatedAt)
              .Take(1)
              .ExecuteDeleteAsync());

        deleted.ShouldBe(1);

        var (auditor, _) = await fixture.CreateClientWithPermissionsAsync(
            tenant, Guid.Empty, Permissions.Reports.ReconciliationView);

        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/sale-payment-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeFalse();
        report.Discrepancies.ShouldContain(d => d.FinancialImpact == 10.00m);
    }

    private sealed record ReconciliationReport(
        string ReportName,
        int RecordsExamined,
        bool IsClean,
        decimal NetImpact,
        IReadOnlyList<DiscrepancyDto> Discrepancies);

    private sealed record DiscrepancyDto(string Kind, string Reference, string Detail, decimal FinancialImpact);
}
