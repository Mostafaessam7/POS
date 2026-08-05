using Microsoft.EntityFrameworkCore;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Adjustments;

/// <summary>
/// Records a manual stock adjustment through the ledger.
/// </summary>
/// <remarks>
/// The one legitimate hand-driven write into stock: a wastage write-off, a found-stock
/// increase, a correction. It exists as a module service rather than in the endpoint
/// because it owns a rule the caller should not have to know — WHERE THE COST COMES
/// FROM — and because <see cref="StockMovement.Record"/> throws on bad input, so the
/// validation that keeps it from throwing belongs next to it, not scattered across
/// callers.
///
/// The cost rule, stated once:
/// <list type="bullet">
///   <item>An INCREASE adds units at a cost the operator supplies — found stock,
///   a positive correction — and that cost moves the weighted average like any other
///   inbound movement.</item>
///   <item>A DECREASE or WASTAGE removes units at the PREVAILING average. You do not
///   get to choose what written-off stock cost; it cost what the shelf says it cost.
///   Letting the caller supply a cost here would be a way to quietly restate margin.</item>
/// </list>
/// </remarks>
public sealed class StockAdjustmentService(InventoryDbContext db, IStockLedger ledger, IClock clock)
{
    public async Task<Result<StockBalance>> AdjustAsync(
        StockAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity <= 0m)
            return Result<StockBalance>.Failure(InventoryErrors.NegativeCount);

        if (string.IsNullOrWhiteSpace(request.ReasonCode))
            return Result<StockBalance>.Failure(InventoryErrors.ReasonCodeRequired);

        var existing = await db.StockBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.WarehouseId == request.WarehouseId && b.VariantId == request.VariantId,
                cancellationToken);

        var (movementType, quantityDelta, unitCost) = request.Kind switch
        {
            StockAdjustmentKind.Increase => ResolveIncrease(request),
            _ => ResolveReduction(request, existing)
        };

        if (unitCost is null)
        {
            // A reduction against a variant that has never had a balance row: there is
            // no prevailing cost to consume at, and inventing one would corrupt the
            // valuation. A manual write-off is for stock the system knows about; the
            // negative balances that arise from sales racing ahead of receipts are a
            // different path entirely.
            return Result<StockBalance>.Failure(InventoryErrors.UnknownVariant);
        }

        var now = clock.UtcNow;

        var reference = new StockDocumentReference(
            StockDocumentType.ManualAdjustment,
            request.AdjustmentId,
            request.Reference);

        var movement = StockMovement.Record(
            db.CurrentTenantId,
            request.WarehouseId,
            request.VariantId,
            movementType,
            quantityDelta,
            unitCost.Value,
            reference,
            now,
            request.BusinessDate,
            terminalId: null,
            userId: request.UserId,
            reasonCode: request.ReasonCode,
            notes: request.Notes);

        // Warn, not Block: back-office movements may drive stock negative and are
        // reported as an exception rather than refused (ADR 027).
        var recorded = await ledger.RecordAsync(movement, NegativeStockPolicy.Warn, cancellationToken);

        if (recorded.IsFailure)
            return Result<StockBalance>.Failure(recorded.Error);

        var balance = await db.StockBalances
            .AsNoTracking()
            .FirstAsync(
                b => b.WarehouseId == request.WarehouseId && b.VariantId == request.VariantId,
                cancellationToken);

        return Result<StockBalance>.Success(balance);
    }

    private static (MovementType Type, decimal QuantityDelta, Money? UnitCost) ResolveIncrease(
        StockAdjustmentRequest request) =>
        // The supplied cost is trusted for an increase — the operator is stating what
        // the added units cost. A validator upstream has already required it.
        (MovementType.AdjustmentIncrease, request.Quantity, request.UnitCost);

    private static (MovementType Type, decimal QuantityDelta, Money? UnitCost) ResolveReduction(
        StockAdjustmentRequest request,
        StockBalance? existing)
    {
        var type = request.Kind == StockAdjustmentKind.Wastage
            ? MovementType.Wastage
            : MovementType.AdjustmentDecrease;

        // Prevailing average, or nothing when there is no balance to draw it from — the
        // caller sees UnknownVariant in that case rather than a fabricated cost.
        return (type, -request.Quantity, existing?.AverageUnitCost);
    }
}

/// <summary>What a manual adjustment does to stock.</summary>
public enum StockAdjustmentKind
{
    /// <summary>Add units at a supplied cost. Found stock, a positive correction.</summary>
    Increase = 1,

    /// <summary>Remove units at the prevailing average. A negative correction.</summary>
    Decrease = 2,

    /// <summary>Remove units at the prevailing average, recorded as a write-off.</summary>
    Wastage = 3
}

/// <summary>A request to adjust stock by hand.</summary>
public sealed record StockAdjustmentRequest
{
    /// <summary>The adjustment's own identity, which becomes the ledger document reference.</summary>
    public required Guid AdjustmentId { get; init; }

    public required Guid WarehouseId { get; init; }

    public required Guid VariantId { get; init; }

    public required StockAdjustmentKind Kind { get; init; }

    /// <summary>Always positive. Direction comes from <see cref="Kind"/>.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Required for an increase, ignored for a reduction.</summary>
    public Money? UnitCost { get; init; }

    public required string ReasonCode { get; init; }

    public required DateOnly BusinessDate { get; init; }

    public string? Reference { get; init; }

    public string? Notes { get; init; }

    public Guid? UserId { get; init; }
}
