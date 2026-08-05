using POS.Inventory.Domain;
using POS.SharedKernel;

namespace POS.Inventory.Ledger;

/// <summary>
/// Writes stock movements and keeps the materialised balance in step.
/// </summary>
/// <remarks>
/// <para>
/// This is the deliberate, documented exception to "no repository pattern over EF Core"
/// (ADR 009). The rule exists because a repository over <c>DbContext</c> wraps a
/// unit-of-work in another unit-of-work and buys nothing. This interface is different
/// on both counts:
/// </para>
/// <list type="number">
///   <item>It has two genuinely different implementations — SQL Server in the cloud and
///   SQLite on the terminal — which is the actual justification for an abstraction, as
///   opposed to the hypothetical database swap that never happens.</item>
///   <item>Its fast path is not expressible in LINQ at all. A lock-free relative
///   <c>UPDATE ... SET Quantity = Quantity - @n</c> has no EF equivalent; going through
///   the change tracker would reintroduce the read-modify-write this design exists to
///   avoid.</item>
/// </list>
/// </remarks>
public interface IStockLedger
{
    /// <summary>
    /// Records a movement and updates the balance atomically.
    /// </summary>
    /// <remarks>
    /// Both writes are in one transaction. The ledger row is the authority; if the
    /// balance is ever wrong it is rebuilt from the ledger, never the other way round.
    /// </remarks>
    public Task<Result> RecordAsync(StockMovement movement, NegativeStockPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Records several movements in one transaction — a sale's lines, or a stocktake's
    /// adjustments — so a sale can never be half-applied to stock.
    /// </summary>
    public Task<Result> RecordBatchAsync(
        IReadOnlyCollection<StockMovement> movements,
        NegativeStockPolicy policy,
        CancellationToken ct = default);

    public Task<StockBalance?> GetBalanceAsync(Guid warehouseId, Guid variantId, CancellationToken ct = default);

    public Task<IReadOnlyDictionary<Guid, decimal>> GetOnHandAsync(
        Guid warehouseId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken ct = default);
}

/// <summary>
/// Recomputes balances from the ledger. Both the repair tool and the nightly audit.
/// </summary>
/// <remarks>
/// The existence of this type is what makes the whole design defensible: because every
/// balance is derivable, a balance bug is a recoverable inconvenience rather than a
/// permanent loss of truth. It is also the Phase 4 gate — a rebuild must reproduce the
/// stored balance exactly.
/// </remarks>
public interface IStockBalanceRebuilder
{
    public Task<StockBalance> RebuildAsync(Guid warehouseId, Guid variantId, CancellationToken ct = default);

    /// <summary>Compares stored balances against the ledger and reports divergence.</summary>
    public Task<IReadOnlyList<BalanceDivergence>> ReconcileAsync(Guid warehouseId, CancellationToken ct = default);
}

public sealed record BalanceDivergence(
    Guid WarehouseId,
    Guid VariantId,
    decimal StoredQuantity,
    decimal LedgerQuantity,
    Money StoredValue,
    Money LedgerValue)
{
    public decimal QuantityDifference => StoredQuantity - LedgerQuantity;
}
