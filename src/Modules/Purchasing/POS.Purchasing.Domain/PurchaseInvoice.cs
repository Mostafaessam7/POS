using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// The supplier's demand for payment, and the third leg of the match.
/// </summary>
/// <remarks>
/// Held as its own aggregate and — importantly — recorded <em>before</em> it is matched.
/// An invoice that fails matching is not discarded; it sits in an exceptions queue with
/// its variances attached, because the supplier believes they are owed the money and
/// somebody has to reconcile that belief with ours. Systems that only persist invoices
/// once they match end up tracking the disputed ones in a spreadsheet.
/// </remarks>
public sealed class PurchaseInvoice : AggregateRoot<Guid>, ITenantScoped, ICompanyScoped
{
    private readonly List<PurchaseInvoiceLine> _lines = [];

    private PurchaseInvoice() { }

    public static PurchaseInvoice Record(
        Guid tenantId,
        Guid companyId,
        Guid supplierId,
        Guid purchaseOrderId,
        string supplierInvoiceNumber,
        string currency,
        DateOnly invoiceDate,
        DateOnly dueDate,
        DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierInvoiceNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new PurchaseInvoice
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            CompanyId = companyId,
            SupplierId = supplierId,
            PurchaseOrderId = purchaseOrderId,
            SupplierInvoiceNumber = supplierInvoiceNumber.Trim(),
            Currency = currency,
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            RecordedAt = recordedAt,
            Status = PurchaseInvoiceStatus.Recorded
        };
    }

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid SupplierId { get; private set; }
    public Guid PurchaseOrderId { get; private set; }

    /// <summary>
    /// The supplier's number, not ours.
    /// </summary>
    /// <remarks>
    /// Unique per supplier per company, and enforced by index rather than by a check in
    /// code. Duplicate-invoice payment is one of the most common ways money leaves a
    /// business by accident, usually because the same invoice arrived once by post and
    /// once by email.
    /// </remarks>
    public string SupplierInvoiceNumber { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;
    public DateOnly InvoiceDate { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public PurchaseInvoiceStatus Status { get; private set; }

    /// <summary>Why the invoice is blocked, in the words of the match that blocked it.</summary>
    public string? BlockReason { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    public IReadOnlyList<PurchaseInvoiceLine> Lines => _lines;

    public byte[] RowVersion { get; private set; } = [];

    public Money NetTotal =>
        _lines.Count == 0
            ? Money.Zero(Currency)
            : _lines.Aggregate(Money.Zero(Currency), (sum, l) => sum + l.LineTotal);

    public Result AddLine(int purchaseOrderLineNumber, Guid variantId, decimal quantity, Money unitPrice)
    {
        if (Status is PurchaseInvoiceStatus.Approved or PurchaseInvoiceStatus.Paid)
        {
            return Result.Failure(PurchasingErrors.InvoiceNotEditable);
        }

        if (quantity <= 0m)
        {
            return Result.Failure(PurchasingErrors.InvoiceQuantityMustBePositive);
        }

        if (unitPrice.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        _lines.Add(new PurchaseInvoiceLine(purchaseOrderLineNumber, variantId, quantity, unitPrice));
        return Result.Success();
    }

    /// <summary>Records the outcome of a three-way match against this invoice.</summary>
    public void ApplyMatch(ThreeWayMatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Status = result.Outcome switch
        {
            MatchOutcome.Matched => PurchaseInvoiceStatus.Matched,
            MatchOutcome.MatchedWithinTolerance => PurchaseInvoiceStatus.Matched,
            _ => PurchaseInvoiceStatus.Blocked
        };

        BlockReason = result.Outcome == MatchOutcome.Blocked
            ? string.Join("; ", result.Variances.Select(v => v.Describe()))
            : null;
    }

    /// <summary>
    /// Approves the invoice for payment.
    /// </summary>
    /// <remarks>
    /// Only a matched invoice may be approved. A blocked one has to be resolved first —
    /// by a credit note, a corrected receipt, or a deliberate override that is itself
    /// recorded. Permitting approval straight from <see cref="PurchaseInvoiceStatus.Blocked"/>
    /// would make the entire matching exercise decorative.
    /// </remarks>
    public Result Approve(Guid userId, DateTimeOffset approvedAt)
    {
        if (Status != PurchaseInvoiceStatus.Matched)
        {
            return Result.Failure(PurchasingErrors.InvoiceNotMatched);
        }

        Status = PurchaseInvoiceStatus.Approved;
        ApprovedByUserId = userId;
        ApprovedAt = approvedAt;
        return Result.Success();
    }

    /// <summary>
    /// Overrides a block, on the authority of a named person and with a stated reason.
    /// </summary>
    /// <remarks>
    /// Overrides exist because reality does: a supplier's 3-cent rounding difference is
    /// not worth a week of correspondence. The control is not that overrides are
    /// impossible, it is that they are attributable. The reason is mandatory and the user
    /// is recorded, so the override report answers "who has been waving invoices through"
    /// without an investigation.
    /// </remarks>
    public Result OverrideBlock(Guid userId, string reason, DateTimeOffset overriddenAt)
    {
        if (Status != PurchaseInvoiceStatus.Blocked)
        {
            return Result.Failure(PurchasingErrors.InvoiceNotBlocked);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.OverrideReasonRequired);
        }

        Status = PurchaseInvoiceStatus.Approved;
        ApprovedByUserId = userId;
        ApprovedAt = overriddenAt;
        BlockReason = $"Overridden: {reason.Trim()}";
        return Result.Success();
    }

    public Result MarkPaid()
    {
        if (Status != PurchaseInvoiceStatus.Approved)
        {
            return Result.Failure(PurchasingErrors.InvoiceNotApproved);
        }

        Status = PurchaseInvoiceStatus.Paid;
        return Result.Success();
    }
}

public sealed record PurchaseInvoiceLine(
    int PurchaseOrderLineNumber,
    Guid VariantId,
    decimal Quantity,
    Money UnitPrice)
{
    /// <inheritdoc cref="GoodsReceiptLine()"/>
    private PurchaseInvoiceLine() : this(0, Guid.Empty, 0m, default) { }

    public Money LineTotal => UnitPrice * Quantity;
}

public enum PurchaseInvoiceStatus
{
    Recorded = 1,
    Matched = 2,
    Blocked = 3,
    Approved = 4,
    Paid = 5
}

/// <summary>
/// Compares what was ordered, what arrived, and what we are being billed for.
/// </summary>
/// <remarks>
/// A pure function, like <see cref="LandedCostAllocator"/> and the settlement reconciler
/// in Phase 6: three sets of lines in, a result out. No I/O, no aggregate mutation. The
/// caller decides what to do with the verdict.
///
/// The two legs are checked against different documents on purpose, and this is the whole
/// point of a three-way match rather than a two-way one:
///
/// <list type="bullet">
/// <item><b>Quantity</b> is checked against the <em>receipt</em>, because what we owe for
/// is what we actually took delivery of. Checking billed quantity against the order would
/// pay for goods that never arrived.</item>
/// <item><b>Price</b> is checked against the <em>order</em>, because the price is what was
/// agreed before the goods shipped. Checking it against the receipt would accept whatever
/// price the supplier chose to write on the delivery note.</item>
/// </list>
///
/// Getting these the wrong way round produces a system that matches happily and pays for
/// the wrong things.
/// </remarks>
public static class ThreeWayMatcher
{
    public static ThreeWayMatchResult Match(
        PurchaseOrder order,
        IReadOnlyList<GoodsReceipt> receipts,
        PurchaseInvoice invoice,
        MatchTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(tolerance);

        var variances = new List<MatchVariance>();

        if (invoice.Currency != order.Currency)
        {
            variances.Add(new MatchVariance(
                0,
                MatchVarianceType.Currency,
                0m,
                0m,
                Money.Zero(invoice.Currency)));

            return new ThreeWayMatchResult(MatchOutcome.Blocked, variances);
        }

        // Receipts are summed per order line: an invoice normally covers several partial
        // deliveries, and matching it against any one of them would fail on every real
        // supply chain.
        var receivedByLine = receipts
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.PurchaseOrderLineNumber)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived));

        foreach (var invoiceLine in invoice.Lines)
        {
            var orderLine = order.Lines.FirstOrDefault(l => l.LineNumber == invoiceLine.PurchaseOrderLineNumber);

            if (orderLine is null)
            {
                variances.Add(new MatchVariance(
                    invoiceLine.PurchaseOrderLineNumber,
                    MatchVarianceType.NoSuchOrderLine,
                    invoiceLine.Quantity,
                    0m,
                    invoiceLine.LineTotal));
                continue;
            }

            receivedByLine.TryGetValue(invoiceLine.PurchaseOrderLineNumber, out var received);

            if (received == 0m)
            {
                // Billed for something that has not arrived at all. Always a block,
                // regardless of tolerance — tolerance is for measurement noise, not for
                // goods that do not exist.
                variances.Add(new MatchVariance(
                    invoiceLine.PurchaseOrderLineNumber,
                    MatchVarianceType.NothingReceived,
                    invoiceLine.Quantity,
                    0m,
                    invoiceLine.LineTotal));
                continue;
            }

            if (invoiceLine.Quantity > received && !tolerance.PermitsQuantityVariance(received, invoiceLine.Quantity))
            {
                variances.Add(new MatchVariance(
                    invoiceLine.PurchaseOrderLineNumber,
                    MatchVarianceType.Quantity,
                    invoiceLine.Quantity,
                    received,
                    invoiceLine.UnitPrice * (invoiceLine.Quantity - received)));
            }

            if (invoiceLine.UnitPrice != orderLine.UnitPrice
                && !tolerance.PermitsPriceVariance(orderLine.UnitPrice, invoiceLine.UnitPrice))
            {
                variances.Add(new MatchVariance(
                    invoiceLine.PurchaseOrderLineNumber,
                    MatchVarianceType.Price,
                    invoiceLine.UnitPrice.Amount,
                    orderLine.UnitPrice.Amount,
                    (invoiceLine.UnitPrice - orderLine.UnitPrice) * invoiceLine.Quantity));
            }
        }

        if (variances.Count > 0)
        {
            return new ThreeWayMatchResult(MatchOutcome.Blocked, variances);
        }

        // Distinguishing an exact match from one that only passed because of tolerance is
        // not pedantry: a supplier whose invoices are permanently 1.9% high under a 2%
        // tolerance is a commercial problem, and it is invisible if both outcomes are
        // reported as "matched".
        var withinToleranceOnly = invoice.Lines.Any(invoiceLine =>
        {
            var orderLine = order.Lines.FirstOrDefault(l => l.LineNumber == invoiceLine.PurchaseOrderLineNumber);
            if (orderLine is null)
            {
                return false;
            }

            receivedByLine.TryGetValue(invoiceLine.PurchaseOrderLineNumber, out var received);
            return invoiceLine.UnitPrice != orderLine.UnitPrice || invoiceLine.Quantity != received;
        });

        return new ThreeWayMatchResult(
            withinToleranceOnly ? MatchOutcome.MatchedWithinTolerance : MatchOutcome.Matched,
            variances);
    }
}

/// <summary>
/// How much disagreement we will absorb rather than escalate.
/// </summary>
/// <remarks>
/// Tolerances are asymmetric by design: they apply only where the supplier has billed
/// <em>more</em> than expected. Being under-billed is not a problem requiring a tolerance
/// — it is a discrepancy we are content to accept, and blocking payment because a supplier
/// charged us too little would be an odd use of anyone's afternoon.
/// </remarks>
public sealed class MatchTolerance
{
    public MatchTolerance(decimal pricePercentage, Money priceAbsolute, decimal quantityPercentage, decimal quantityAbsolute)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pricePercentage);
        ArgumentOutOfRangeException.ThrowIfNegative(quantityPercentage);
        ArgumentOutOfRangeException.ThrowIfNegative(quantityAbsolute);
        if (priceAbsolute.IsNegative)
            throw new ArgumentOutOfRangeException(nameof(priceAbsolute));

        PricePercentage = pricePercentage;
        PriceAbsolute = priceAbsolute;
        QuantityPercentage = quantityPercentage;
        QuantityAbsolute = quantityAbsolute;
    }

    public decimal PricePercentage { get; }
    public Money PriceAbsolute { get; }
    public decimal QuantityPercentage { get; }
    public decimal QuantityAbsolute { get; }

    public bool PermitsPriceVariance(Money agreed, Money billed)
    {
        if (billed <= agreed)
        {
            return true;
        }

        var excess = billed - agreed;

        if (excess <= PriceAbsolute)
        {
            return true;
        }

        return agreed.Amount > 0m && excess.Amount / agreed.Amount * 100m <= PricePercentage;
    }

    public bool PermitsQuantityVariance(decimal received, decimal billed)
    {
        if (billed <= received)
        {
            return true;
        }

        var excess = billed - received;

        if (excess <= QuantityAbsolute)
        {
            return true;
        }

        return received > 0m && excess / received * 100m <= QuantityPercentage;
    }

    public static MatchTolerance Strict(string currency) => new(0m, Money.Zero(currency), 0m, 0m);

    /// <summary>2% or one minor unit on price, 2% or one unit on quantity.</summary>
    public static MatchTolerance Default(string currency) => new(2m, new Money(0.01m, currency), 2m, 1m);
}

public sealed record ThreeWayMatchResult(MatchOutcome Outcome, IReadOnlyList<MatchVariance> Variances)
{
    public bool IsPayable => Outcome is MatchOutcome.Matched or MatchOutcome.MatchedWithinTolerance;
}

public enum MatchOutcome
{
    Matched = 1,
    MatchedWithinTolerance = 2,
    Blocked = 3
}

public sealed record MatchVariance(
    int PurchaseOrderLineNumber,
    MatchVarianceType Type,
    decimal Billed,
    decimal Expected,
    Money FinancialImpact)
{
    public string Describe() => Type switch
    {
        MatchVarianceType.Quantity => $"Line {PurchaseOrderLineNumber}: billed {Billed}, received {Expected}",
        MatchVarianceType.Price => $"Line {PurchaseOrderLineNumber}: billed {Billed}, agreed {Expected}",
        MatchVarianceType.NothingReceived => $"Line {PurchaseOrderLineNumber}: billed {Billed}, nothing received",
        MatchVarianceType.NoSuchOrderLine => $"Line {PurchaseOrderLineNumber}: not on the purchase order",
        MatchVarianceType.Currency => "Invoice currency does not match the order",
        _ => $"Line {PurchaseOrderLineNumber}: unmatched"
    };
}

public enum MatchVarianceType
{
    Quantity = 1,
    Price = 2,
    NothingReceived = 3,
    NoSuchOrderLine = 4,
    Currency = 5
}
