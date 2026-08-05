using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Fiscal.Domain;
using POS.Fiscal.Persistence;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Synced sales become fiscal documents.
/// </summary>
/// <remarks>
/// The fiscal module was fully implemented, registered, and referenced by nothing: a
/// pipeline, a generic profile, a gap-free allocator and a document store that had never
/// issued a document. These tests are what turn that from code into behaviour.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class SaleFiscalisationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Every_synced_sale_gets_exactly_one_fiscal_document()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 3);

        var response = await fixture.UploadAsync(tenant, batch);
        response.Accepted.ShouldBe(3);

        var documents = await fixture.ReadAsync<FiscalDbContext, List<FiscalDocument>>(tenant, db =>
            db.Documents.AsNoTracking().ToListAsync());

        documents.Count.ShouldBe(3);

        // Issued, not Accepted: the generic profile has no transmitter, because a
        // jurisdiction with no clearance mandate has nobody to transmit to. Issued is
        // terminal there (ADR 031).
        documents.ShouldAllBe(d => d.Status == FiscalDocumentStatus.Issued);

        // The document carries the seller's identity from the company record, not from
        // configuration — which is what allows one platform to serve merchants in
        // different countries.
        documents.ShouldAllBe(d => d.ProfileCode == "GENERIC");

        // The signed bytes are stored as issued. A regenerated document is a different
        // document: the hash chain is over these exact bytes.
        documents.ShouldAllBe(d => d.Content.Length > 0);
        documents.ShouldAllBe(d => d.CanonicalHash.Length > 0);
    }

    /// <summary>
    /// Fiscal numbers are gap-free and sequential per terminal series.
    /// </summary>
    /// <remarks>
    /// Both a gap and a duplicate in a fiscal series are findings an auditor raises, so
    /// this asserts the actual sequence rather than merely that the numbers differ.
    /// </remarks>
    [Fact]
    public async Task Fiscal_numbers_are_sequential_and_gap_free()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 5));

        var sequences = await fixture.ReadAsync<FiscalDbContext, List<long>>(tenant, db =>
            db.Documents.AsNoTracking().OrderBy(d => d.Sequence).Select(d => d.Sequence).ToListAsync());

        sequences.ShouldBe([1, 2, 3, 4, 5]);
    }

    /// <summary>
    /// A replayed batch issues no second document.
    /// </summary>
    /// <remarks>
    /// Worse than a duplicate row: a second document CONSUMES A NUMBER out of a gap-free
    /// series, so the damage is permanent and visible to an auditor. The adapter is
    /// idempotent by sale for exactly this reason.
    /// </remarks>
    [Fact]
    public async Task Replaying_a_batch_issues_no_second_document()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 4);

        await fixture.UploadAsync(tenant, batch);
        var replay = await fixture.UploadAsync(tenant, batch);

        replay.Accepted.ShouldBe(0);
        replay.Duplicates.ShouldBe(4);

        var count = await fixture.ReadAsync<FiscalDbContext, int>(tenant, db =>
            db.Documents.CountAsync());

        count.ShouldBe(4);
    }

    /// <summary>The sale ↔ fiscal reconciliation is clean once sales are fiscalised.</summary>
    [Fact]
    public async Task Sale_fiscal_reconciliation_is_clean()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 2));

        var (auditor, _) = await fixture.CreateClientWithPermissionsAsync(
            tenant, Guid.Empty, Permissions.Reports.ReconciliationView);

        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/sale-fiscal-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();

        // Both sides: two sales plus the two documents they produced. This reconciler
        // counts what it EXAMINED, not what it iterated — unlike the receipt one, which
        // counts receipt lines only because the ledger side is looked up per line.
        report.RecordsExamined.ShouldBe(4);
        report.IsClean.ShouldBeTrue();
    }

    /// <summary>
    /// A sale whose document never got issued is reported.
    /// </summary>
    /// <remarks>
    /// This is the gap the design knowingly leaves: fiscalisation runs after the sale is
    /// durable and never rejects the record, because refusing an upload would leave
    /// money taken with no record of the sale. The report is what makes that trade
    /// safe — so it has to be shown catching the case, not merely shown agreeing.
    /// </remarks>
    [Fact]
    public async Task A_sale_without_a_fiscal_document_is_reported()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 2));

        // Simulates the crash window between the sale save and the document being
        // issued. Fiscal documents are immutable once issued, so this deletes below the
        // aggregate rather than through it.
        var deleted = await fixture.ReadAsync<FiscalDbContext, int>(tenant, db =>
            db.Documents.Where(d => d.Sequence == 1).ExecuteDeleteAsync());

        deleted.ShouldBe(1);

        var (auditor, _) = await fixture.CreateClientWithPermissionsAsync(
            tenant, Guid.Empty, Permissions.Reports.ReconciliationView);

        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/sale-fiscal-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeFalse();
        report.Discrepancies.Count.ShouldBe(1);
    }

    private sealed record ReconciliationReport(
        string ReportName,
        int RecordsExamined,
        bool IsClean,
        decimal NetImpact,
        IReadOnlyList<DiscrepancyDto> Discrepancies);

    private sealed record DiscrepancyDto(string Kind, string Reference, string Detail, decimal FinancialImpact);
}
