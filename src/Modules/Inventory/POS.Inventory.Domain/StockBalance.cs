using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// Materialised on-hand quantity and value for one variant in one warehouse.
/// </summary>
/// <remarks>
/// <para>
/// A projection, not a source of truth. Everything here is derivable from
/// <see cref="StockMovement"/>; it exists so that "how many do we have?" is an indexed
/// single-row read rather than a SUM over millions of ledger rows. If it ever disagrees
/// with the ledger, the ledger wins and this row is rebuilt.
/// </para>
/// <para>
/// Balances may legitimately be NEGATIVE. See ADR 027: refusing a sale because the
/// system believes stock is zero, while the customer is holding the item, loses a real
/// sale to fix a data error the refusal does not fix. A negative balance is a reliable
/// signal of an unbooked delivery and is reported as an exception, not prevented.
/// </para>
/// </remarks>
public sealed class StockBalance : Entity<Guid>, ITenantScoped
{
    private StockBalance() { }

    public static StockBalance Empty(Guid tenantId, Guid warehouseId, Guid variantId, string currency) => new()
    {
        Id = SequentialId.New(),
        TenantId = tenantId,
        WarehouseId = warehouseId,
        VariantId = variantId,
        QuantityOnHand = 0m,
        AverageUnitCost = Money.Zero(currency),
        TotalValue = Money.Zero(currency)
    };

    public Guid TenantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid VariantId { get; private set; }

    public decimal QuantityOnHand { get; private set; }

    /// <summary>
    /// Weighted average cost per unit, held at FULL decimal precision.
    /// </summary>
    /// <remarks>
    /// Deliberately not rounded to currency precision. Rounding a unit cost to two
    /// decimals and multiplying back up by quantity reintroduces exactly the drift
    /// <see cref="Money"/> exists to prevent — on 10,000 units a half-penny of rounding
    /// is £50 of phantom stock value. Round for display only. See ADR 020.
    /// </remarks>
    public Money AverageUnitCost { get; private set; }

    public Money TotalValue { get; private set; }

    /// <summary>Optimistic concurrency token, used only on the cost-changing path.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public DateTimeOffset LastMovementAt { get; private set; }

    public bool IsNegative => QuantityOnHand < 0m;

    /// <summary>
    /// Applies an inbound movement, recalculating the weighted average.
    /// </summary>
    /// <remarks>
    /// <para>
    /// New average = (existing value + incoming value) / (existing quantity + incoming
    /// quantity). This is read-modify-write and genuinely requires a lock, because the
    /// result depends on the current state. It is confined to inbound movements, which
    /// are low-frequency back-office actions, keeping the contended path away from
    /// checkout. See ADR 026.
    /// </para>
    /// <para>
    /// The negative-stock case is the subtle one. If on-hand is −5 (goods sold before
    /// the delivery was booked) and 10 arrive, the resulting quantity is 5 and the
    /// existing "value" of the negative stock is meaningless. Blending it produces a
    /// distorted average, so the incoming cost is adopted wholesale instead. This is
    /// the standard treatment and it self-corrects once the missing receipt is entered.
    /// </para>
    /// </remarks>
    public void ApplyInbound(decimal quantity, Money unitCost, DateTimeOffset occurredAt)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Inbound quantity must be positive.");

        var newQuantity = QuantityOnHand + quantity;

        if (QuantityOnHand <= 0m || newQuantity <= 0m)
        {
            // Existing value is meaningless or the result is still negative: adopt the
            // incoming cost rather than blending against a nonsense base.
            AverageUnitCost = unitCost;
        }
        else
        {
            var incomingValue = unitCost * quantity;
            AverageUnitCost = (TotalValue + incomingValue) / newQuantity;
        }

        QuantityOnHand = newQuantity;
        TotalValue = AverageUnitCost * QuantityOnHand;
        LastMovementAt = occurredAt;
    }

    /// <summary>
    /// Applies an outbound movement at the prevailing average cost.
    /// </summary>
    /// <remarks>
    /// Does not change <see cref="AverageUnitCost"/> — consuming stock does not alter
    /// what the remaining stock cost. That property is what allows the production path
    /// to use a lock-free relative UPDATE; this method exists for in-memory use and for
    /// the rebuilder, not for the hot checkout path.
    /// </remarks>
    public void ApplyOutbound(decimal quantity, DateTimeOffset occurredAt)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Outbound quantity must be positive.");

        QuantityOnHand -= quantity;
        TotalValue = AverageUnitCost * QuantityOnHand;
        LastMovementAt = occurredAt;
    }

    /// <summary>
    /// Adds value to (or removes value from) the stock on hand without moving any units.
    /// </summary>
    /// <remarks>
    /// This is the landed-cost path, and it takes a <em>value delta</em> rather than a new
    /// average deliberately. When a freight invoice arrives you know what it cost, not what
    /// the units are now worth per unit; making the caller compute a new average first
    /// pushes the division — and the rounding error — outside the aggregate that owns the
    /// invariant.
    ///
    /// If quantity on hand is zero there is nothing left to revalue. That is not an error:
    /// it means everything received was sold before the freight invoice arrived, and the
    /// whole cost belongs in variance. The caller decides that; this method simply refuses
    /// to divide by zero and leaves the average untouched.
    /// </remarks>
    public void ApplyValueAdjustment(Money valueDelta, DateTimeOffset occurredAt)
    {
        TotalValue += valueDelta;

        if (QuantityOnHand != 0m)
        {
            AverageUnitCost = TotalValue / QuantityOnHand;
        }

        LastMovementAt = occurredAt;
    }

    /// <summary>Revalues without changing quantity — a cost correction.</summary>
    public void Revalue(Money newAverageUnitCost, DateTimeOffset occurredAt)
    {
        AverageUnitCost = newAverageUnitCost;
        TotalValue = newAverageUnitCost * QuantityOnHand;
        LastMovementAt = occurredAt;
    }
}

/// <summary>
/// What to do when a movement would drive stock below zero.
/// </summary>
/// <remarks>
/// Defaults to <see cref="Allow"/> for sales, deliberately. See ADR 027.
/// </remarks>
public enum NegativeStockPolicy
{
    /// <summary>Permit and report as an exception. The default for sales.</summary>
    Allow = 0,

    /// <summary>Permit, but require an explicit acknowledgement. The default for back-office movements.</summary>
    Warn = 1,

    /// <summary>Reject. For controlled or serialised goods only.</summary>
    Block = 2
}
