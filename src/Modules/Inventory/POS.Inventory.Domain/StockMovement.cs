using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// One immutable entry in the stock ledger. Append-only: never updated, never deleted.
/// </summary>
/// <remarks>
/// <para>
/// The ledger is the authority for stock. <see cref="StockBalance"/> is a projection of
/// it and can always be rebuilt. A mistake is corrected by a compensating movement, not
/// by editing history — the same rule financial documents follow in ADR 007.
/// </para>
/// <para>
/// <b>Quantity is a signed delta, never a resulting balance.</b> That choice looks like a
/// detail and is in fact the reason offline works. A movement saying "quantity is now 7",
/// generated on a till that was disconnected for three days, would silently overwrite
/// every sale, delivery and transfer that happened in the meantime when it finally
/// arrived. A delta of −1 produces the same balance whenever it is applied and in
/// whatever order it arrives relative to its peers. The ledger design and the offline
/// design are therefore the same decision. See ADR 004 and ADR 025.
/// </para>
/// </remarks>
public sealed class StockMovement : Entity<Guid>, ITenantScoped
{
    private StockMovement() { }

    public static StockMovement Record(
        Guid tenantId,
        Guid warehouseId,
        Guid variantId,
        MovementType type,
        decimal quantityDelta,
        Money unitCost,
        StockDocumentReference reference,
        DateTimeOffset occurredAt,
        DateOnly businessDate,
        Guid? terminalId,
        Guid? userId,
        string? reasonCode = null,
        string? notes = null)
    {
        if (quantityDelta == 0m)
        {
            // A zero movement carries no information and pollutes reconciliation.
            // If something genuinely happened with no quantity effect (a cost-only
            // revaluation), use RecordValueAdjustment, which is explicit.
            throw new ArgumentException(
                "A stock movement must change quantity. Use RecordValueAdjustment for value-only changes.",
                nameof(quantityDelta));
        }

        if (type == MovementType.CostAdjustment)
        {
            // Guarded from the other side too: a value-only type must not arrive on the
            // quantity path carrying a quantity. Either half alone would let the two
            // factories drift into overlapping responsibilities.
            throw new ArgumentException(
                "CostAdjustment is a value-only movement. Use RecordValueAdjustment.",
                nameof(type));
        }

        if (unitCost.IsNegative)
        {
            throw new ArgumentException("Unit cost cannot be negative.", nameof(unitCost));
        }

        EnsureSignMatchesType(type, quantityDelta);

        return new StockMovement
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            VariantId = variantId,
            Type = type,
            QuantityDelta = quantityDelta,
            UnitCost = unitCost,
            TotalCost = unitCost * Math.Abs(quantityDelta),
            Reference = reference,
            OccurredAt = occurredAt,
            BusinessDate = businessDate,
            TerminalId = terminalId,
            UserId = userId,
            ReasonCode = reasonCode,
            Notes = notes
        };
    }

    /// <summary>
    /// Records a value-only movement: the stock is worth more (or less) than we thought,
    /// but no units moved.
    /// </summary>
    /// <remarks>
    /// A separate factory rather than a nullable quantity on <see cref="Record"/>, because
    /// the two have genuinely different invariants. <see cref="Record"/> requires a
    /// non-zero quantity and a non-negative cost; this requires a zero quantity and
    /// permits a negative value. Folding them together would mean every rule in both
    /// becoming conditional on the type — which is how a ledger stops being trustworthy.
    ///
    /// <paramref name="valueDelta"/> may be negative: a supplier credit for over-billed
    /// freight genuinely reduces the value of stock already on hand.
    /// </remarks>
    public static StockMovement RecordValueAdjustment(
        Guid tenantId,
        Guid warehouseId,
        Guid variantId,
        Money valueDelta,
        StockDocumentReference reference,
        DateTimeOffset occurredAt,
        DateOnly businessDate,
        Guid? userId,
        string reasonCode,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        if (valueDelta.IsZero)
        {
            // Same reasoning as the zero-quantity rule: a movement that changes nothing
            // is noise in the audit trail and a false positive in reconciliation.
            throw new ArgumentException(
                "A value adjustment must change value.",
                nameof(valueDelta));
        }

        return new StockMovement
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            VariantId = variantId,
            Type = MovementType.CostAdjustment,
            QuantityDelta = 0m,
            UnitCost = Money.Zero(valueDelta.Currency),
            TotalCost = valueDelta,
            Reference = reference,
            OccurredAt = occurredAt,
            BusinessDate = businessDate,
            TerminalId = null,
            UserId = userId,
            ReasonCode = reasonCode,
            Notes = notes
        };
    }

    public Guid TenantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid VariantId { get; private set; }

    public MovementType Type { get; private set; }

    /// <summary>Signed change in quantity. Negative for outbound. Never an absolute balance.</summary>
    public decimal QuantityDelta { get; private set; }

    /// <summary>
    /// Cost per unit at the moment of the movement.
    /// </summary>
    /// <remarks>
    /// For inbound movements this is the purchase cost and it feeds the weighted
    /// average. For outbound movements it is the prevailing average at the time of
    /// consumption, captured here so that cost of goods sold is a historical fact and
    /// does not change retroactively when a later delivery shifts the average.
    /// </remarks>
    public Money UnitCost { get; private set; }

    public Money TotalCost { get; private set; }

    /// <summary>What caused this movement — a sale, a receipt, a transfer, a count.</summary>
    public StockDocumentReference Reference { get; private set; } = null!;

    /// <summary>Wall-clock instant, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    /// The trading day this belongs to, which is not the same as the calendar date of
    /// <see cref="OccurredAt"/> for a store trading past midnight. Reports group by this.
    /// See ADR 017.
    /// </summary>
    public DateOnly BusinessDate { get; private set; }

    public Guid? TerminalId { get; private set; }
    public Guid? UserId { get; private set; }

    public string? ReasonCode { get; private set; }
    public string? Notes { get; private set; }

    public bool IsInbound => QuantityDelta > 0m;

    private static void EnsureSignMatchesType(MovementType type, decimal delta)
    {
        // Catches the transposition bug — recording a sale as +1 — at the point of
        // creation rather than during a stocktake three weeks later.
        var expectInbound = type.IsInbound();

        if (expectInbound is true && delta < 0m)
            throw new ArgumentException($"{type} is an inbound movement and requires a positive quantity.");

        if (expectInbound is false && delta > 0m)
            throw new ArgumentException($"{type} is an outbound movement and requires a negative quantity.");
    }
}

/// <summary>
/// What kind of event moved the stock. Drives both sign validation and whether the
/// movement is allowed to change weighted average cost.
/// </summary>
public enum MovementType
{
    /// <summary>Goods received from a supplier. Sets cost.</summary>
    Receipt = 1,

    /// <summary>Sold to a customer. Consumes at the prevailing average.</summary>
    Sale = 2,

    /// <summary>Customer return placed back into sellable stock.</summary>
    CustomerReturn = 3,

    /// <summary>Returned to the supplier.</summary>
    SupplierReturn = 4,

    /// <summary>Dispatched on an inter-warehouse transfer.</summary>
    TransferOut = 5,

    /// <summary>Received from an inter-warehouse transfer.</summary>
    TransferIn = 6,

    /// <summary>Damage, expiry, theft, or other write-off.</summary>
    Wastage = 7,

    /// <summary>Manual increase, requiring a reason code.</summary>
    AdjustmentIncrease = 8,

    /// <summary>Manual decrease, requiring a reason code.</summary>
    AdjustmentDecrease = 9,

    /// <summary>Variance arising from a physical count.</summary>
    StocktakeAdjustment = 10,

    /// <summary>Consumed as a component when a bundle or assembly was sold.</summary>
    ComponentConsumption = 11,

    /// <summary>
    /// A value-only change: stock value moves, quantity does not.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a landed cost that arrives after the goods — a freight
    /// or duty invoice three weeks behind the delivery. The units are already on the shelf
    /// and already counted; what changed is what they cost.
    ///
    /// This is the only movement type permitted to carry a zero quantity delta, and the
    /// only one whose <see cref="StockMovement.TotalCost"/> may be negative (a supplier
    /// credit reduces value). Created through
    /// <see cref="StockMovement.RecordValueAdjustment"/>, never through
    /// <see cref="StockMovement.Record"/>.
    ///
    /// Added in Phase 7. Phase 4 already referenced this type by name in the zero-quantity
    /// error message but never declared it — the guidance pointed at a member that did not
    /// exist. See ADR 047.
    /// </remarks>
    CostAdjustment = 12
}

public static class MovementTypeExtensions
{
    /// <summary>
    /// True for inbound, false for outbound, null where either direction is valid.
    /// </summary>
    public static bool? IsInbound(this MovementType type) => type switch
    {
        MovementType.Receipt => true,
        MovementType.CustomerReturn => true,
        MovementType.TransferIn => true,
        MovementType.AdjustmentIncrease => true,

        MovementType.Sale => false,
        MovementType.SupplierReturn => false,
        MovementType.TransferOut => false,
        MovementType.Wastage => false,
        MovementType.AdjustmentDecrease => false,
        MovementType.ComponentConsumption => false,

        // A count can go either way.
        MovementType.StocktakeAdjustment => null,

        // Neither: no units move at all.
        MovementType.CostAdjustment => null,

        _ => null
    };

    /// <summary>
    /// Whether this movement may alter weighted average cost.
    /// </summary>
    /// <remarks>
    /// This predicate is what makes checkout scale. Movements that cannot change cost
    /// take the lock-free relative-update path; only these take a row lock. Sales are
    /// deliberately absent — a sale consumes stock at the prevailing average and does
    /// not alter it — which keeps the hot path off the contended route entirely.
    /// See ADR 026.
    /// </remarks>
    public static bool AffectsAverageCost(this MovementType type) => type switch
    {
        MovementType.Receipt => true,
        MovementType.CustomerReturn => true,
        MovementType.TransferIn => true,
        MovementType.AdjustmentIncrease => true,

        // A landed cost changes value without changing quantity, which changes the
        // average by definition. It must take the row lock like any other cost-affecting
        // movement, and must not be routed down the lock-free relative-update path.
        MovementType.CostAdjustment => true,

        _ => false
    };

    /// <summary>Manual movements that must carry a reason code for audit.</summary>
    public static bool RequiresReasonCode(this MovementType type) => type is
        MovementType.Wastage or
        MovementType.AdjustmentIncrease or
        MovementType.AdjustmentDecrease or
        MovementType.StocktakeAdjustment or
        MovementType.CostAdjustment;
}

/// <summary>
/// Pointer back to the business document that caused a movement.
/// </summary>
/// <remarks>
/// Deliberately a loose reference — a document type and an id — rather than a foreign
/// key per source. Inventory must not take a hard dependency on Sales, Purchasing and
/// Transfers; that would defeat the module boundaries of ADR 002 and make the ledger
/// undeployable without every other module present.
/// </remarks>
public sealed record StockDocumentReference(
    StockDocumentType DocumentType,
    Guid DocumentId,
    string? DocumentNumber);

public enum StockDocumentType
{
    Sale = 1,
    PurchaseReceipt = 2,
    PurchaseOrder = 7,
    SupplierReturn = 8,
    LandedCost = 9,
    PurchaseInvoice = 10,
    Transfer = 3,
    Stocktake = 4,
    ManualAdjustment = 5,
    Return = 6
}
