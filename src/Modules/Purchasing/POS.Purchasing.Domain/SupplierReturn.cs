using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// Goods going back to the supplier, and the credit we expect in exchange.
/// </summary>
/// <remarks>
/// The return and the credit note are deliberately separate concerns on one aggregate.
/// Sending goods back is a stock event and happens on the day the courier collects; the
/// credit note is a financial event and arrives whenever the supplier gets round to it.
/// Treating them as one thing means either the stock leaves before we have a document to
/// justify it, or the goods sit in a corner of the warehouse until the paperwork catches
/// up. Both happen in practice, and both are avoidable.
///
/// The gap between them — dispatched, not yet credited — is the report that gets money
/// back. Suppliers do not chase themselves.
/// </remarks>
public sealed class SupplierReturn : AggregateRoot<Guid>, ITenantScoped, IBranchScoped
{
    private readonly List<SupplierReturnLine> _lines = [];

    private SupplierReturn() { }

    public static SupplierReturn Create(
        Guid tenantId,
        Guid branchId,
        Guid warehouseId,
        Guid supplierId,
        string returnNumber,
        string currency,
        SupplierReturnReason reason,
        Guid raisedByUserId,
        DateTimeOffset raisedAt,
        BusinessDate businessDate,
        Guid? originalGoodsReceiptId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new SupplierReturn
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            BranchId = branchId,
            WarehouseId = warehouseId,
            SupplierId = supplierId,
            ReturnNumber = returnNumber,
            Currency = currency,
            Reason = reason,
            RaisedByUserId = raisedByUserId,
            RaisedAt = raisedAt,
            BusinessDate = businessDate,
            OriginalGoodsReceiptId = originalGoodsReceiptId,
            Status = SupplierReturnStatus.Draft
        };
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid SupplierId { get; private set; }

    public string ReturnNumber { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;
    public SupplierReturnReason Reason { get; private set; }

    /// <summary>
    /// The delivery being returned against, where one is known.
    /// </summary>
    /// <remarks>
    /// Optional, because a return is not always traceable to a receipt: goods found
    /// damaged at the back of a shelf months later are genuinely from "some delivery".
    /// Where it is known it should be supplied, since it is what lets the return be
    /// valued at the cost actually paid rather than at whatever the current average is.
    /// </remarks>
    public Guid? OriginalGoodsReceiptId { get; private set; }

    public Guid RaisedByUserId { get; private set; }
    public DateTimeOffset RaisedAt { get; private set; }
    public BusinessDate BusinessDate { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }

    public SupplierReturnStatus Status { get; private set; }

    /// <summary>The supplier's credit note number, once it arrives.</summary>
    public string? CreditNoteNumber { get; private set; }
    public Money? CreditedAmount { get; private set; }
    public DateOnly? CreditNoteDate { get; private set; }

    public IReadOnlyList<SupplierReturnLine> Lines => _lines;

    public byte[] RowVersion { get; private set; } = [];

    public Money ExpectedCredit =>
        _lines.Count == 0
            ? Money.Zero(Currency)
            : _lines.Aggregate(Money.Zero(Currency), (sum, l) => sum + l.LineValue);

    /// <summary>
    /// The difference between what we expected and what the supplier actually credited.
    /// </summary>
    /// <remarks>
    /// Positive means they short-credited us. This is the number the report is for.
    /// </remarks>
    public Money? CreditShortfall =>
        CreditedAmount is null ? null : ExpectedCredit - CreditedAmount.Value;

    public Result AddLine(Guid variantId, decimal quantity, Money unitCost)
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            return Result.Failure(PurchasingErrors.ReturnNotEditable);
        }

        if (quantity <= 0m)
        {
            return Result.Failure(PurchasingErrors.ReturnQuantityMustBePositive);
        }

        if (unitCost.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        _lines.Add(new SupplierReturnLine(variantId, quantity, unitCost));
        return Result.Success();
    }

    /// <summary>
    /// Dispatches the goods, producing the outbound stock movements.
    /// </summary>
    /// <remarks>
    /// Like a goods receipt, this yields plain instructions rather than writing to
    /// Inventory itself, so the module boundary holds and the effect is assertable in a
    /// unit test.
    /// </remarks>
    public Result<SupplierReturnPosting> Dispatch(DateTimeOffset dispatchedAt)
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            return Result<SupplierReturnPosting>.Failure(PurchasingErrors.ReturnNotEditable);
        }

        if (_lines.Count == 0)
        {
            return Result<SupplierReturnPosting>.Failure(PurchasingErrors.ReturnHasNoLines);
        }

        Status = SupplierReturnStatus.Dispatched;
        DispatchedAt = dispatchedAt;

        var movements = _lines
            .Select(l => new StockReturnInstruction(l.VariantId, l.Quantity, l.UnitCost))
            .ToList();

        return Result<SupplierReturnPosting>.Success(new SupplierReturnPosting(
            Id,
            ReturnNumber,
            WarehouseId,
            BusinessDate,
            dispatchedAt,
            movements));
    }

    /// <summary>
    /// Records the credit note the supplier eventually issued.
    /// </summary>
    /// <remarks>
    /// The credited amount is recorded as received, not as expected. A supplier who
    /// credits less than we claimed has not settled the matter, and overwriting our figure
    /// with theirs would erase the disagreement — which is the one piece of information
    /// worth keeping.
    /// </remarks>
    public Result RecordCreditNote(string creditNoteNumber, Money amount, DateOnly creditNoteDate)
    {
        if (Status is not (SupplierReturnStatus.Dispatched or SupplierReturnStatus.PartiallyCredited))
        {
            return Result.Failure(PurchasingErrors.ReturnNotAwaitingCredit);
        }

        if (string.IsNullOrWhiteSpace(creditNoteNumber))
        {
            return Result.Failure(PurchasingErrors.CreditNoteNumberRequired);
        }

        if (amount.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        if (amount.IsNegative)
        {
            return Result.Failure(PurchasingErrors.CreditCannotBeNegative);
        }

        CreditNoteNumber = creditNoteNumber.Trim();
        CreditedAmount = amount;
        CreditNoteDate = creditNoteDate;

        Status = amount >= ExpectedCredit
            ? SupplierReturnStatus.Credited
            : SupplierReturnStatus.PartiallyCredited;

        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            // Once the goods have left the building the return is a fact, not a plan.
            return Result.Failure(PurchasingErrors.ReturnNotEditable);
        }

        Status = SupplierReturnStatus.Cancelled;
        return Result.Success();
    }
}

public sealed record SupplierReturnLine(Guid VariantId, decimal Quantity, Money UnitCost)
{
    /// <inheritdoc cref="GoodsReceiptLine()"/>
    private SupplierReturnLine() : this(Guid.Empty, 0m, default) { }

    public Money LineValue => UnitCost * Quantity;
}

public enum SupplierReturnStatus
{
    Draft = 1,
    Dispatched = 2,
    PartiallyCredited = 3,
    Credited = 4,
    Cancelled = 5
}

public enum SupplierReturnReason
{
    Damaged = 1,
    WrongItem = 2,
    Overstock = 3,
    Expired = 4,
    QualityRejection = 5,
    Other = 99
}

public sealed record SupplierReturnPosting(
    Guid SupplierReturnId,
    string ReturnNumber,
    Guid WarehouseId,
    BusinessDate BusinessDate,
    DateTimeOffset DispatchedAt,
    IReadOnlyList<StockReturnInstruction> Movements);

public sealed record StockReturnInstruction(Guid VariantId, decimal Quantity, Money UnitCost);
