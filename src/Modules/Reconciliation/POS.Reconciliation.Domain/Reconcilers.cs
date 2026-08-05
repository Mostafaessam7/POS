using POS.SharedKernel;

namespace POS.Reconciliation.Domain;

// ---------------------------------------------------------------------------------
// Inputs
//
// Every reconciler takes plain projections, never another module's aggregates. A
// reconciliation that referenced Sales, Fiscal, Payments and Purchasing types would be a
// module that depends on everything, which is precisely the coupling ADR 002 exists to
// prevent — and it would be undeployable without all four present.
//
// The application layer projects each module's own tables into these records. That
// projection is the only place that knows both shapes, and it is thin enough to read.
// ---------------------------------------------------------------------------------

/// <summary>A completed sale, as far as reconciliation is concerned.</summary>
public sealed record SaleProjection(
    Guid SaleId,
    string ReceiptNumber,
    Guid TerminalId,
    DateOnly BusinessDate,
    Money Total,
    bool IsVoided);

/// <summary>A fiscal document issued for a sale.</summary>
public sealed record FiscalDocumentProjection(
    Guid DocumentId,
    Guid SaleId,
    DateOnly BusinessDate,
    Money Total,
    bool IsSubmitted);

/// <summary>A payment recorded against a sale.</summary>
public sealed record PaymentProjection(
    Guid PaymentId,
    Guid SaleId,
    DateOnly BusinessDate,
    Money Amount,
    Money RefundedAmount,
    PaymentReconciliationStatus Status);

public enum PaymentReconciliationStatus
{
    Captured = 1,
    Failed = 2,
    Indeterminate = 3,
    Voided = 4
}

/// <summary>A posted goods receipt line, from Purchasing.</summary>
public sealed record ReceiptLineProjection(
    Guid GoodsReceiptId,
    string ReceiptNumber,
    Guid VariantId,
    decimal Quantity,
    Money LandedValue);

/// <summary>A stock movement recorded against a purchase receipt, from Inventory.</summary>
public sealed record StockMovementProjection(
    Guid DocumentId,
    Guid VariantId,
    decimal Quantity,
    Money TotalCost);

/// <summary>A supplier return that has left the building.</summary>
public sealed record SupplierReturnProjection(
    Guid ReturnId,
    string ReturnNumber,
    Guid SupplierId,
    DateOnly DispatchedOn,
    Money ExpectedCredit,
    Money CreditedAmount);

// ---------------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------------

/// <summary>
/// One thing that does not agree, described well enough to act on without opening a
/// database.
/// </summary>
public sealed record Discrepancy(
    DiscrepancyKind Kind,
    string Reference,
    string Detail,
    Money FinancialImpact)
{
    public override string ToString() => $"{Kind}: {Reference} — {Detail}";
}

public enum DiscrepancyKind
{
    /// <summary>A sale exists with no fiscal document. Money taken, nothing declared.</summary>
    SaleWithoutFiscalDocument = 1,

    /// <summary>A fiscal document exists with no sale. Declared something that never happened.</summary>
    FiscalDocumentWithoutSale = 2,

    /// <summary>Both exist and their totals differ.</summary>
    FiscalTotalMismatch = 3,

    /// <summary>A fiscal document that was never submitted to the authority.</summary>
    FiscalDocumentNotSubmitted = 4,

    /// <summary>A sale exists with no payment covering it.</summary>
    SaleWithoutPayment = 5,

    /// <summary>A payment references a sale that does not exist.</summary>
    PaymentWithoutSale = 6,

    /// <summary>Payments captured against a sale do not sum to its total.</summary>
    PaymentTotalMismatch = 7,

    /// <summary>A payment whose outcome is still unknown.</summary>
    PaymentUnresolved = 8,

    /// <summary>Goods received but no stock movement recorded.</summary>
    ReceiptWithoutStockMovement = 9,

    /// <summary>Stock movement quantity or value disagrees with the receipt.</summary>
    ReceiptMovementMismatch = 10,

    /// <summary>Goods returned to a supplier with no credit note received.</summary>
    ReturnWithoutCredit = 11,

    /// <summary>A credit note received for less than the value returned.</summary>
    CreditShortfall = 12
}

/// <summary>
/// The result of one reconciliation run.
/// </summary>
/// <remarks>
/// <see cref="IsClean"/> is deliberately "no discrepancies", not "the money nets to zero".
/// A run containing an unrecorded 50 and a phantom 50 nets to nothing and is not clean; it
/// is two separate problems that happen to be the same size. The distinction was learned
/// in Phase 6 (ADR 044) and applies to every reconciliation in the system.
/// </remarks>
public sealed record ReconciliationResult(
    string ReportName,
    DateOnly BusinessDate,
    int RecordsExamined,
    IReadOnlyList<Discrepancy> Discrepancies)
{
    public bool IsClean => Discrepancies.Count == 0;

    public Money NetImpact(string currency) =>
        Discrepancies.Aggregate(Money.Zero(currency), (sum, d) => sum + d.FinancialImpact);
}

// ---------------------------------------------------------------------------------
// Sale ↔ Fiscal
// ---------------------------------------------------------------------------------

/// <summary>
/// Answers the question the tax authority will eventually ask: does every sale have a
/// fiscal document, and does every fiscal document have a sale?
/// </summary>
/// <remarks>
/// `FiscalDocument.SaleId` carries no foreign key, because Fiscal must not take a hard
/// dependency on Sales (ADR 002 and the ERD). The database therefore cannot enforce the
/// relationship, and this report is what stands in its place. It has been documented as
/// mandatory since Phase 5 and is only now built.
/// </remarks>
public static class SaleFiscalReconciler
{
    public static ReconciliationResult Reconcile(
        IReadOnlyList<SaleProjection> sales,
        IReadOnlyList<FiscalDocumentProjection> documents,
        DateOnly businessDate,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(documents);

        var discrepancies = new List<Discrepancy>();
        var documentsBySale = documents.GroupBy(d => d.SaleId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var sale in sales)
        {
            // A voided sale legitimately has no fiscal document — nothing was sold.
            if (sale.IsVoided)
            {
                continue;
            }

            if (!documentsBySale.TryGetValue(sale.SaleId, out var matched))
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.SaleWithoutFiscalDocument,
                    sale.ReceiptNumber,
                    $"Sale of {sale.Total} on terminal {sale.TerminalId} has no fiscal document",
                    sale.Total));
                continue;
            }

            var declared = matched.Aggregate(Money.Zero(currency), (sum, d) => sum + d.Total);
            if (declared != sale.Total)
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.FiscalTotalMismatch,
                    sale.ReceiptNumber,
                    $"Sale totals {sale.Total}, fiscal documents total {declared}",
                    declared - sale.Total));
            }

            foreach (var unsubmitted in matched.Where(d => !d.IsSubmitted))
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.FiscalDocumentNotSubmitted,
                    sale.ReceiptNumber,
                    $"Fiscal document {unsubmitted.DocumentId} was issued but never submitted",
                    Money.Zero(currency)));
            }
        }

        var saleIds = sales.Select(s => s.SaleId).ToHashSet();
        foreach (var orphan in documents.Where(d => !saleIds.Contains(d.SaleId)))
        {
            // Worse than the reverse in one specific way: we have declared tax on something
            // there is no record of selling, and cannot explain it if asked.
            discrepancies.Add(new Discrepancy(
                DiscrepancyKind.FiscalDocumentWithoutSale,
                orphan.DocumentId.ToString(),
                $"Fiscal document for {orphan.Total} references sale {orphan.SaleId}, which does not exist",
                orphan.Total));
        }

        return new ReconciliationResult(
            "Sale ↔ Fiscal",
            businessDate,
            sales.Count + documents.Count,
            discrepancies);
    }
}

// ---------------------------------------------------------------------------------
// Sale ↔ Payment
// ---------------------------------------------------------------------------------

/// <summary>
/// Does every sale have payment covering it, and does every payment belong to a sale?
/// </summary>
public static class SalePaymentReconciler
{
    public static ReconciliationResult Reconcile(
        IReadOnlyList<SaleProjection> sales,
        IReadOnlyList<PaymentProjection> payments,
        DateOnly businessDate,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(payments);

        var discrepancies = new List<Discrepancy>();
        var paymentsBySale = payments.GroupBy(p => p.SaleId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var sale in sales)
        {
            if (sale.IsVoided)
            {
                continue;
            }

            paymentsBySale.TryGetValue(sale.SaleId, out var matched);
            matched ??= [];

            // Only captured money counts towards covering a sale. Indeterminate payments
            // are reported separately: treating them as paid is how a shop gives away
            // goods, and treating them as failed is how a customer is charged twice.
            var captured = matched
                .Where(p => p.Status == PaymentReconciliationStatus.Captured)
                .Aggregate(Money.Zero(currency), (sum, p) => sum + (p.Amount - p.RefundedAmount));

            if (matched.Count == 0)
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.SaleWithoutPayment,
                    sale.ReceiptNumber,
                    $"Sale of {sale.Total} has no payment of any kind",
                    sale.Total));
            }
            else if (captured != sale.Total)
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.PaymentTotalMismatch,
                    sale.ReceiptNumber,
                    $"Sale totals {sale.Total}, captured payments total {captured}",
                    captured - sale.Total));
            }

            foreach (var unresolved in matched.Where(p => p.Status == PaymentReconciliationStatus.Indeterminate))
            {
                // Every row here is a customer who may have been charged for a sale we
                // cannot prove, or a sale we cannot prove was paid for (ADR 044).
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.PaymentUnresolved,
                    sale.ReceiptNumber,
                    $"Payment {unresolved.PaymentId} for {unresolved.Amount} is still indeterminate",
                    unresolved.Amount));
            }
        }

        var saleIds = sales.Select(s => s.SaleId).ToHashSet();
        foreach (var orphan in payments.Where(p => !saleIds.Contains(p.SaleId)))
        {
            discrepancies.Add(new Discrepancy(
                DiscrepancyKind.PaymentWithoutSale,
                orphan.PaymentId.ToString(),
                $"Payment of {orphan.Amount} references sale {orphan.SaleId}, which does not exist",
                orphan.Amount));
        }

        return new ReconciliationResult(
            "Sale ↔ Payment",
            businessDate,
            sales.Count + payments.Count,
            discrepancies);
    }
}

// ---------------------------------------------------------------------------------
// Goods receipt ↔ Stock movement
// ---------------------------------------------------------------------------------

/// <summary>
/// Did every posted goods receipt actually reach the stock ledger?
/// </summary>
/// <remarks>
/// ADR 052 has Purchasing hand plain instructions to the application layer, which applies
/// them to Inventory. Nothing at compile time forces that second step to happen. This
/// report is the agreed discharge of that exposure, and without it the decision buys
/// nothing.
/// </remarks>
public static class ReceiptStockReconciler
{
    public static ReconciliationResult Reconcile(
        IReadOnlyList<ReceiptLineProjection> receiptLines,
        IReadOnlyList<StockMovementProjection> movements,
        DateOnly businessDate,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(receiptLines);
        ArgumentNullException.ThrowIfNull(movements);

        var discrepancies = new List<Discrepancy>();

        var movementsByKey = movements
            .GroupBy(m => (m.DocumentId, m.VariantId))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var line in receiptLines)
        {
            if (!movementsByKey.TryGetValue((line.GoodsReceiptId, line.VariantId), out var matched))
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.ReceiptWithoutStockMovement,
                    line.ReceiptNumber,
                    $"Received {line.Quantity} of variant {line.VariantId} worth {line.LandedValue}, but no stock movement exists",
                    line.LandedValue));
                continue;
            }

            var quantity = matched.Sum(m => m.Quantity);
            var value = matched.Aggregate(Money.Zero(currency), (sum, m) => sum + m.TotalCost);

            if (quantity != line.Quantity || value != line.LandedValue)
            {
                discrepancies.Add(new Discrepancy(
                    DiscrepancyKind.ReceiptMovementMismatch,
                    line.ReceiptNumber,
                    $"Receipt says {line.Quantity} @ {line.LandedValue}, ledger says {quantity} @ {value}",
                    value - line.LandedValue));
            }
        }

        return new ReconciliationResult(
            "Goods receipt ↔ Stock ledger",
            businessDate,
            receiptLines.Count,
            discrepancies);
    }
}

// ---------------------------------------------------------------------------------
// Supplier return ↔ Credit note
// ---------------------------------------------------------------------------------

/// <summary>
/// Goods went back. Did the money come back?
/// </summary>
/// <remarks>
/// This is the report ADR 054 was designed to make possible, and the only one on this list
/// that recovers cash rather than merely proving correctness. Storing the credited amount
/// as received rather than as expected is what allows the two figures to disagree, and the
/// disagreement is the whole product.
/// </remarks>
public static class SupplierCreditReconciler
{
    public static ReconciliationResult Reconcile(
        IReadOnlyList<SupplierReturnProjection> returns,
        DateOnly businessDate,
        string currency,
        int gracePeriodDays = 30)
    {
        ArgumentNullException.ThrowIfNull(returns);

        var discrepancies = new List<Discrepancy>();

        foreach (var supplierReturn in returns)
        {
            var shortfall = supplierReturn.ExpectedCredit - supplierReturn.CreditedAmount;

            if (!shortfall.IsZero && !shortfall.IsNegative)
            {
                var age = businessDate.DayNumber - supplierReturn.DispatchedOn.DayNumber;

                if (supplierReturn.CreditedAmount.IsZero)
                {
                    // Nothing at all. Chased hard once past the grace period, but reported
                    // from day one — a return that is going to be ignored is usually
                    // identifiable long before it is thirty days old.
                    discrepancies.Add(new Discrepancy(
                        DiscrepancyKind.ReturnWithoutCredit,
                        supplierReturn.ReturnNumber,
                        age > gracePeriodDays
                            ? $"No credit received {age} days after dispatch — past the {gracePeriodDays}-day terms"
                            : $"No credit received yet, {age} days after dispatch",
                        shortfall));
                }
                else
                {
                    discrepancies.Add(new Discrepancy(
                        DiscrepancyKind.CreditShortfall,
                        supplierReturn.ReturnNumber,
                        $"Returned {supplierReturn.ExpectedCredit}, credited {supplierReturn.CreditedAmount}",
                        shortfall));
                }
            }
        }

        return new ReconciliationResult(
            "Supplier return ↔ Credit note",
            businessDate,
            returns.Count,
            discrepancies);
    }
}
