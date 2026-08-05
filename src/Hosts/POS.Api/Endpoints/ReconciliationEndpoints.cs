using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Identity.Authorization;
using POS.Fiscal.Domain;
using POS.Fiscal.Persistence;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.Payments.Domain;
using POS.Payments.Persistence;
using POS.Purchasing.Domain;
using POS.Purchasing.Persistence;
using POS.Reconciliation.Domain;
using POS.Sales.Domain;
using POS.Sales.Persistence;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// The reconciliation reports: does the system agree with itself?
/// </summary>
/// <remarks>
/// <para>
/// ASSEMBLED IN THE HOST, deliberately. Each reconciler compares two modules' views of
/// the same event, so no module can own the query — Purchasing must not read Inventory's
/// ledger and Inventory must not read Purchasing's receipts. The composition root is the
/// one place that legitimately sees both, which is exactly why
/// <c>POS.Reconciliation.Domain</c> takes plain projections and depends on nothing
/// (ADR 002).
/// </para>
/// <para>
/// These are the safety net for the stock posting seam. That code is idempotent and
/// correctly ordered, but "correct by construction" is a claim; a report that walks the
/// documents against the ledger is evidence. It is also the only thing that would catch
/// a movement lost to a crash in the window between the two commits.
/// </para>
/// <para>
/// ALL FOUR RECONCILERS ARE NOW EXPOSED. Each waited until both sides of its comparison
/// had real data — Sale ↔ Payment last, because a report that flags every sale as unpaid
/// before anything records payments trains people to ignore it, and that is the one
/// failure a control like this cannot survive.
/// </para>
/// </remarks>
public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/reports")
                       .RequireAuthorization()
                       .RequirePermission(Permissions.Reports.ReconciliationView);

        // Goods receipt ↔ stock ledger. Every posted receipt should have moved exactly
        // the quantity and value it claims.
        group.MapGet("/receipt-stock-reconciliation", async (
            DateOnly businessDate,
            string? currency,
            PurchasingDbContext purchasing,
            InventoryDbContext inventory,
            CancellationToken ct) =>
        {
            var receipts = await purchasing.GoodsReceipts
                .AsNoTracking()
                .Include(r => r.Lines)
                .Include(r => r.LandedCosts)
                .Where(r => r.Status == GoodsReceiptStatus.Posted)
                .ToListAsync(ct);

            // BusinessDate is a value object converted to a date column, so the filter
            // runs in memory rather than as a translated predicate. Acceptable at this
            // scale and honest about it; a reporting projection is the fix if the
            // receipt table ever outgrows it.
            receipts = [.. receipts.Where(r => r.BusinessDate.Value == businessDate)];

            var reportCurrency = currency ?? receipts.FirstOrDefault()?.Currency ?? "USD";

            var receiptLines = receipts
                .SelectMany(r => r.ProjectLandedCost().Select(instruction => new ReceiptLineProjection(
                    r.Id,
                    r.ReceiptNumber,
                    instruction.VariantId,
                    instruction.Quantity,

                    // Goods value plus this line's share of freight and duty — the
                    // figure the ledger should have recorded as TotalCost.
                    instruction.LandedUnitCost * instruction.Quantity)))
                .ToList();

            var receiptIds = receipts.ConvertAll(r => r.Id);

            var movements = await inventory.StockMovements
                .AsNoTracking()
                .Where(m => receiptIds.Contains(m.Reference.DocumentId))
                .Select(m => new { m.Reference.DocumentId, m.VariantId, m.QuantityDelta, m.TotalCost })
                .ToListAsync(ct);

            var movementProjections = movements
                .ConvertAll(m => new StockMovementProjection(m.DocumentId, m.VariantId, m.QuantityDelta, m.TotalCost));

            var result = ReceiptStockReconciler.Reconcile(
                receiptLines, movementProjections, businessDate, reportCurrency);

            return Results.Ok(ToResponse(result, reportCurrency));
        });

        // Supplier return ↔ credit note. The only report here that recovers cash rather
        // than merely proving correctness (ADR 054).
        group.MapGet("/supplier-credit-reconciliation", async (
            DateOnly businessDate,
            string? currency,
            int? gracePeriodDays,
            PurchasingDbContext purchasing,
            CancellationToken ct) =>
        {
            var dispatched = await purchasing.SupplierReturns
                .AsNoTracking()
                .Include(r => r.Lines)
                .Where(r => r.DispatchedAt != null)
                .ToListAsync(ct);

            var reportCurrency = currency ?? dispatched.FirstOrDefault()?.Currency ?? "USD";

            var projections = dispatched.ConvertAll(r => new SupplierReturnProjection(
                r.Id,
                r.ReturnNumber,
                r.SupplierId,
                DateOnly.FromDateTime(r.DispatchedAt!.Value.UtcDateTime),
                r.ExpectedCredit,

                // Zero, not null, when no credit note has arrived. The reconciler's job
                // is to measure the shortfall, and "nothing received" is a shortfall of
                // the full amount rather than an absent measurement.
                r.CreditedAmount ?? Money.Zero(r.Currency)));

            var result = SupplierCreditReconciler.Reconcile(
                projections, businessDate, reportCurrency, gracePeriodDays ?? 30);

            return Results.Ok(ToResponse(result, reportCurrency));
        });

        // Sale ↔ fiscal document. Every completed sale should have exactly one document,
        // and the document should agree with the sale about the money.
        group.MapGet("/sale-fiscal-reconciliation", async (
            DateOnly businessDate,
            string? currency,
            SalesDbContext sales,
            FiscalDbContext fiscal,
            CancellationToken ct) =>
        {
            var completed = await sales.Sales
                .AsNoTracking()
                .Where(s => s.BusinessDate == businessDate
                         && (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Voided))
                .ToListAsync(ct);

            var reportCurrency = currency ?? completed.FirstOrDefault()?.Currency ?? "USD";

            var saleProjections = completed.ConvertAll(s => new SaleProjection(
                s.Id,
                s.ReceiptNumber.ToString(),
                s.TerminalId,
                s.BusinessDate,
                s.TotalInclusiveTax,
                s.Status == SaleStatus.Voided));

            var saleIds = completed.ConvertAll(s => s.Id);

            var documents = await fiscal.Documents
                .AsNoTracking()
                .Where(d => saleIds.Contains(d.SaleId))
                .Select(d => new { d.Id, d.SaleId, d.BusinessDate, d.Status })
                .ToListAsync(ct);

            var documentProjections = documents.ConvertAll(d => new FiscalDocumentProjection(
                d.Id,
                d.SaleId,
                d.BusinessDate,

                // The document's total is not stored as a scalar — it lives inside the
                // signed content, which must not be re-parsed to produce a report. The
                // sale's own total is used, so this reconciler answers "is there a
                // document, and is it in the right state", not "do two copies of the
                // total agree". Comparing totals needs the profile to expose one, which
                // is a plugin concern rather than a reporting one.
                completed.First(s => s.Id == d.SaleId).TotalInclusiveTax,

                d.Status != FiscalDocumentStatus.Rejected));

            var result = SaleFiscalReconciler.Reconcile(
                saleProjections, documentProjections, businessDate, reportCurrency);

            return Results.Ok(ToResponse(result, reportCurrency));
        });

        // Sale ↔ payment. Does the electronic money taken match the electronic money
        // owed? Cash is out of scope — it is drawer accountability, reconciled by the
        // shift, and has none of the auth/capture/settle lifecycle this report walks.
        group.MapGet("/sale-payment-reconciliation", async (
            DateOnly businessDate,
            string? currency,
            SalesDbContext sales,
            PaymentsDbContext paymentsDb,
            CancellationToken ct) =>
        {
            var completed = await sales.Sales
                .AsNoTracking()
                .Include(s => s.Tenders)
                .Where(s => s.BusinessDate == businessDate
                         && (s.Status == SaleStatus.Completed || s.Status == SaleStatus.Voided))
                .ToListAsync(ct);

            var reportCurrency = currency ?? completed.FirstOrDefault()?.Currency ?? "USD";

            // Only sales with an electronic tender are in scope. A fully-cash sale has no
            // electronic payment to reconcile, so including it would manufacture a
            // "SaleWithoutPayment" discrepancy for a sale that was correctly paid.
            var electronicSales = completed
                .Where(s => s.Tenders.Any(t => t.Method != TenderMethod.Cash))
                .ToList();

            var saleProjections = electronicSales.ConvertAll(s => new SaleProjection(
                s.Id,
                s.ReceiptNumber.ToString(),
                s.TerminalId,
                s.BusinessDate,

                // The ELECTRONIC subtotal, not the sale total: the cash portion is not
                // this report's concern, so the payments recorded should cover exactly
                // the non-cash tenders.
                s.Tenders.Where(t => t.Method != TenderMethod.Cash)
                         .Aggregate(Money.Zero(s.Currency), (sum, t) => sum + t.Amount),

                s.Status == SaleStatus.Voided));

            var saleIds = electronicSales.ConvertAll(s => s.Id);

            var payments = await paymentsDb.Payments
                .AsNoTracking()
                .Where(p => saleIds.Contains(p.SaleId))
                .Select(p => new { p.Id, p.SaleId, p.BusinessDate, p.Amount, p.RefundedAmount, p.Status })
                .ToListAsync(ct);

            var paymentProjections = payments.ConvertAll(p => new PaymentProjection(
                p.Id,
                p.SaleId,
                p.BusinessDate.Value,
                p.Amount,
                p.RefundedAmount,
                ToReconciliationStatus(p.Status)));

            var result = SalePaymentReconciler.Reconcile(
                saleProjections, paymentProjections, businessDate, reportCurrency);

            return Results.Ok(ToResponse(result, reportCurrency));
        });

        // Stock balance ↔ ledger. Inventory checking itself: the materialised balance
        // must be reproducible from the append-only movements it was built from, which
        // is what makes a balance bug recoverable rather than a loss of truth.
        group.MapGet("/stock-balance-reconciliation", async (
            Guid warehouseId,
            IStockBalanceRebuilder rebuilder,
            CancellationToken ct) =>
        {
            var divergences = await rebuilder.ReconcileAsync(warehouseId, ct);

            return Results.Ok(new
            {
                warehouseId,
                isClean = divergences.Count == 0,
                divergences = divergences.Select(d => new
                {
                    d.VariantId,
                    d.StoredQuantity,
                    d.LedgerQuantity,
                    d.QuantityDifference,
                    storedValue = d.StoredValue.Amount,
                    ledgerValue = d.LedgerValue.Amount
                })
            });
        });

        return app;
    }

    /// <summary>
    /// Collapses the payment lifecycle to the four states reconciliation cares about.
    /// </summary>
    /// <remarks>
    /// The reconciler asks one question — did the money arrive, definitely not, or
    /// don't-we-know — so the eight-state payment lifecycle folds down. Authorised and
    /// Captured and Settled all mean the money is on its way or here, and count as
    /// Captured. Declined and Failed are definite negatives. Indeterminate stays itself,
    /// because it is the one state the report must never guess at: counting it as paid
    /// gives away goods, counting it as failed charges the customer twice.
    /// </remarks>
    private static PaymentReconciliationStatus ToReconciliationStatus(PaymentStatus status) => status switch
    {
        PaymentStatus.Authorised or PaymentStatus.Captured or PaymentStatus.Settled
            => PaymentReconciliationStatus.Captured,
        PaymentStatus.Declined or PaymentStatus.Failed => PaymentReconciliationStatus.Failed,
        PaymentStatus.Voided => PaymentReconciliationStatus.Voided,
        _ => PaymentReconciliationStatus.Indeterminate
    };

    private static ReconciliationReportResponse ToResponse(ReconciliationResult result, string currency) => new(
        result.ReportName,
        result.BusinessDate,
        result.RecordsExamined,
        result.IsClean,
        result.NetImpact(currency).Amount,
        currency,
        [.. result.Discrepancies.Select(d => new DiscrepancyResponse(
            d.Kind, d.Reference, d.Detail, d.FinancialImpact.Amount))]);
}

public sealed record ReconciliationReportResponse(
    string ReportName,
    DateOnly BusinessDate,
    int RecordsExamined,
    bool IsClean,
    decimal NetImpact,
    string Currency,
    IReadOnlyList<DiscrepancyResponse> Discrepancies);

public sealed record DiscrepancyResponse(
    DiscrepancyKind Kind,
    string Reference,
    string Detail,
    decimal FinancialImpact);
