using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// What actually turned up, as distinct from what was ordered.
/// </summary>
/// <remarks>
/// A separate aggregate from <see cref="PurchaseOrder"/> because one order produces many
/// receipts and each is an independent event with its own date, its own delivery note and
/// its own stock consequences. Modelling receipts as mutable state on the order would
/// leave no way to answer "what arrived on the 14th", which is precisely the question a
/// stock discrepancy investigation starts with.
///
/// The receipt does not write to the stock ledger itself. It produces a
/// <see cref="GoodsReceiptPosting"/> — plain data describing the intended movements — and
/// the application layer hands that to Inventory. Purchasing therefore takes no
/// dependency on Inventory's domain (ADR 002), and the posting can be asserted in a test
/// without a database.
/// </remarks>
public sealed class GoodsReceipt : AggregateRoot<Guid>, ITenantScoped, IBranchScoped
{
    private readonly List<GoodsReceiptLine> _lines = [];
    private readonly List<LandedCostCharge> _landedCosts = [];

    private GoodsReceipt() { }

    public static GoodsReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid warehouseId,
        Guid purchaseOrderId,
        Guid supplierId,
        string receiptNumber,
        string currency,
        string? supplierDeliveryNote,
        Guid receivedByUserId,
        DateTimeOffset receivedAt,
        BusinessDate businessDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new GoodsReceipt
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            BranchId = branchId,
            WarehouseId = warehouseId,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId,
            ReceiptNumber = receiptNumber,
            Currency = currency,
            SupplierDeliveryNote = supplierDeliveryNote?.Trim(),
            ReceivedByUserId = receivedByUserId,
            ReceivedAt = receivedAt,
            BusinessDate = businessDate,
            Status = GoodsReceiptStatus.Draft
        };
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid PurchaseOrderId { get; private set; }
    public Guid SupplierId { get; private set; }

    public string ReceiptNumber { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;

    /// <summary>The supplier's own document number, as printed on the paperwork in the driver's hand.</summary>
    public string? SupplierDeliveryNote { get; private set; }

    public Guid ReceivedByUserId { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public BusinessDate BusinessDate { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }

    public GoodsReceiptStatus Status { get; private set; }

    public IReadOnlyList<GoodsReceiptLine> Lines => _lines;

    /// <summary>Freight, duty and handling attached to this delivery.</summary>
    public IReadOnlyList<LandedCostCharge> LandedCosts => _landedCosts;

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Goods value at supplier prices, before landed costs.</summary>
    public Money GoodsValue =>
        _lines.Count == 0
            ? Money.Zero(Currency)
            : _lines.Aggregate(Money.Zero(Currency), (sum, l) => sum + l.LineValue);

    /// <summary>Landed cost known at the time of posting.</summary>
    public Money LandedCostTotal =>
        _landedCosts.Count == 0
            ? Money.Zero(Currency)
            : _landedCosts.Aggregate(Money.Zero(Currency), (sum, c) => sum + c.Amount);

    public bool IsPosted => Status == GoodsReceiptStatus.Posted;

    public Result AddLine(int purchaseOrderLineNumber, Guid variantId, decimal quantityReceived, Money unitPrice)
    {
        if (IsPosted)
        {
            return Result.Failure(PurchasingErrors.ReceiptAlreadyPosted);
        }

        if (quantityReceived <= 0m)
        {
            return Result.Failure(PurchasingErrors.ReceiptQuantityMustBePositive);
        }

        if (unitPrice.IsNegative)
        {
            return Result.Failure(PurchasingErrors.PriceCannotBeNegative);
        }

        if (unitPrice.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        _lines.Add(new GoodsReceiptLine(
            purchaseOrderLineNumber,
            variantId,
            quantityReceived,
            unitPrice));

        return Result.Success();
    }

    /// <summary>
    /// Attaches a charge that forms part of the cost of getting these goods onto the shelf.
    /// </summary>
    public Result AddLandedCost(LandedCostType type, Money amount, string reference, LandedCostAllocationBasis basis)
    {
        if (IsPosted)
        {
            return Result.Failure(PurchasingErrors.ReceiptAlreadyPosted);
        }

        if (amount.IsNegative || amount.IsZero)
        {
            return Result.Failure(PurchasingErrors.LandedCostMustBePositive);
        }

        if (amount.Currency != Currency)
        {
            return Result.Failure(PurchasingErrors.CurrencyMismatch);
        }

        _landedCosts.Add(new LandedCostCharge(type, amount, reference?.Trim() ?? string.Empty, basis));
        return Result.Success();
    }

    /// <summary>
    /// Applies the receipt to its order and produces the stock movements it implies.
    /// </summary>
    /// <remarks>
    /// Posting is the point of no return: after it the goods are on the shelf and
    /// sellable, so the receipt becomes immutable (D6). Everything that could fail —
    /// tolerance breaches, order status, allocation — is checked before the order is
    /// touched, so a rejected posting leaves no partial effect.
    ///
    /// The unit cost handed to Inventory is the <em>landed</em> unit cost, not the
    /// supplier's price. This is what makes weighted average cost mean anything: a
    /// product bought at 10 with 2 of freight cost 12 to have, and pricing it off 10
    /// produces a margin report that is confidently wrong.
    /// </remarks>
    public Result<GoodsReceiptPosting> Post(PurchaseOrder order, ReceiptTolerance tolerance, DateTimeOffset postedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(tolerance);

        if (IsPosted)
        {
            return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.ReceiptAlreadyPosted);
        }

        if (order.Id != PurchaseOrderId)
        {
            return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.ReceiptOrderMismatch);
        }

        if (_lines.Count == 0)
        {
            return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.ReceiptHasNoLines);
        }

        // Validate every line against the order before mutating anything. A receipt that
        // half-applies is worse than one that is rejected: the second is a message to the
        // storeman, the first is a stock count nobody can explain.
        foreach (var line in _lines)
        {
            var orderLine = order.Lines.FirstOrDefault(l => l.LineNumber == line.PurchaseOrderLineNumber);
            if (orderLine is null)
            {
                return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.OrderLineNotFound);
            }

            if (orderLine.VariantId != line.VariantId)
            {
                return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.ReceiptLineVariantMismatch);
            }

            if (!tolerance.PermitsOverReceipt(orderLine.QuantityOrdered, orderLine.QuantityReceived + line.QuantityReceived))
            {
                return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.OverReceiptExceedsTolerance);
            }
        }

        var allocation = LandedCostAllocator.Allocate(_lines, _landedCosts, Currency);

        foreach (var line in _lines)
        {
            var applied = order.ApplyReceipt(line.PurchaseOrderLineNumber, line.QuantityReceived, tolerance);
            if (applied.IsFailure)
            {
                // Should be unreachable given the pre-validation above; treated as a bug
                // signal rather than a silent partial post.
                return Result<GoodsReceiptPosting>.Failure(applied.Error);
            }
        }

        Status = GoodsReceiptStatus.Posted;
        PostedAt = postedAt;

        var movements = _lines
            .Select((line, index) => new StockReceiptInstruction(
                line.VariantId,
                line.QuantityReceived,
                LandedUnitCost(line, allocation[index]),
                allocation[index]))
            .ToList();

        return Result<GoodsReceiptPosting>.Success(new GoodsReceiptPosting(
            Id,
            ReceiptNumber,
            WarehouseId,
            BusinessDate,
            postedAt,
            movements));
    }

    /// <summary>
    /// What this receipt says each line cost, landed — without posting it.
    /// </summary>
    /// <remarks>
    /// A pure query over stored state, and it exists so the receipt ↔ stock ledger
    /// reconciliation has a receipt-side figure to compare against.
    ///
    /// It deliberately shares <see cref="LandedCostAllocator"/> with <see cref="Post"/>
    /// rather than letting the reporting layer re-derive the apportionment. Two
    /// implementations of the same allocation would eventually disagree, and the report
    /// whose entire job is detecting disagreement would be the last place anyone looked
    /// — it would simply start finding discrepancies that were its own arithmetic.
    /// </remarks>
    public IReadOnlyList<StockReceiptInstruction> ProjectLandedCost()
    {
        var allocation = LandedCostAllocator.Allocate(_lines, _landedCosts, Currency);

        return [.. _lines.Select((line, index) => new StockReceiptInstruction(
            line.VariantId,
            line.QuantityReceived,
            LandedUnitCost(line, allocation[index]),
            allocation[index]))];
    }

    private static Money LandedUnitCost(GoodsReceiptLine line, Money allocatedLandedCost) =>
        (line.LineValue + allocatedLandedCost) / line.QuantityReceived;
}

/// <summary>One variant on a delivery, at the price the supplier charged for it.</summary>
public sealed record GoodsReceiptLine(
    int PurchaseOrderLineNumber,
    Guid VariantId,
    decimal QuantityReceived,
    Money UnitPrice)
{
    /// <summary>
    /// Required by EF Core for materialisation, and non-public so application code
    /// still has to go through the positional constructor.
    /// </summary>
    /// <remarks>
    /// EF Core 9 cannot bind a complex-typed parameter — <see cref="Money"/> — to a
    /// constructor, so it must be able to build the instance empty and write the
    /// properties through their backing fields. Chaining to the primary constructor
    /// keeps every member at a declared-valid value rather than leaving it unassigned.
    /// This is the same accommodation <see cref="Entity{TId}"/> documents.
    /// </remarks>
    private GoodsReceiptLine() : this(0, Guid.Empty, 0m, default) { }

    public Money LineValue => UnitPrice * QuantityReceived;
}

public enum GoodsReceiptStatus
{
    Draft = 1,
    Posted = 2
}

/// <summary>
/// The stock consequences of a posted receipt, expressed without reference to Inventory's types.
/// </summary>
public sealed record GoodsReceiptPosting(
    Guid GoodsReceiptId,
    string ReceiptNumber,
    Guid WarehouseId,
    BusinessDate BusinessDate,
    DateTimeOffset PostedAt,
    IReadOnlyList<StockReceiptInstruction> Movements);

/// <summary>The stock effect of one received line, in terms Inventory can act on.</summary>
/// <param name="VariantId">The variant received.</param>
/// <param name="Quantity">How many units arrived on this delivery.</param>
/// <param name="LandedUnitCost">
/// Supplier price plus this line's share of freight and duty, per unit. This is the figure
/// that enters weighted average cost.
/// </param>
/// <param name="AllocatedLandedCost">
/// This line's share of the delivery's landed costs, kept separately so the allocation can
/// be shown to a buyer asking why the cost is not the price they negotiated.
/// </param>
public sealed record StockReceiptInstruction(
    Guid VariantId,
    decimal Quantity,
    Money LandedUnitCost,
    Money AllocatedLandedCost);
