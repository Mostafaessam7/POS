using Microsoft.EntityFrameworkCore;
using POS.Inventory.Domain;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Ledger;

/// <summary>
/// Recomputes balances from the ledger, and reports any divergence.
/// </summary>
/// <remarks>
/// The safety net that makes the projection design defensible. Because every balance is
/// derivable from immutable movements, a balance bug is a recoverable inconvenience
/// rather than a permanent loss of truth — which is exactly what an in-place mutable
/// quantity column cannot offer.
///
/// This is also the Phase 4 gate: a rebuild must reproduce the stored balance exactly.
/// </remarks>
public sealed class StockBalanceRebuilder : IStockBalanceRebuilder
{
    private readonly InventoryDbContext _db;

    public StockBalanceRebuilder(InventoryDbContext db) => _db = db;

    public async Task<StockBalance> RebuildAsync(Guid warehouseId, Guid variantId, CancellationToken ct = default)
    {
        // Replayed in occurrence order because weighted average is order-dependent:
        // receiving 10 at £1 then 10 at £2 gives £1.50, while the reverse gives the same
        // average only by coincidence of equal quantities. With unequal quantities the
        // order matters, so the replay must follow the ledger's chronology.
        var movements = await _db.StockMovements
            .AsNoTracking()
            .Where(m => m.WarehouseId == warehouseId && m.VariantId == variantId)
            .OrderBy(m => m.OccurredAt)
            .ThenBy(m => m.Id) // UUID v7 is time-ordered, so this is a stable tie-break
            .ToListAsync(ct);

        if (movements.Count == 0)
        {
            throw new InvalidOperationException(
                $"No movements exist for variant {variantId} in warehouse {warehouseId}; nothing to rebuild.");
        }

        var currency = movements[0].UnitCost.Currency;
        var rebuilt = StockBalance.Empty(movements[0].TenantId, warehouseId, variantId, currency);

        foreach (var movement in movements)
        {
            if (movement.IsInbound)
            {
                rebuilt.ApplyInbound(movement.QuantityDelta, movement.UnitCost, movement.OccurredAt);
            }
            else
            {
                rebuilt.ApplyOutbound(Math.Abs(movement.QuantityDelta), movement.OccurredAt);
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// Compares every stored balance in a warehouse against a ledger replay.
    /// </summary>
    /// <remarks>
    /// Runs nightly and alarms on any divergence. A quantity divergence is a defect and
    /// should page someone; a value divergence within a rounding tolerance is expected,
    /// because stored value is a product of two rounded numbers.
    /// </remarks>
    public async Task<IReadOnlyList<BalanceDivergence>> ReconcileAsync(Guid warehouseId, CancellationToken ct = default)
    {
        var ledgerTotals = await _db.StockMovements
            .AsNoTracking()
            .Where(m => m.WarehouseId == warehouseId)
            .GroupBy(m => m.VariantId)
            .Select(g => new { VariantId = g.Key, Quantity = g.Sum(m => m.QuantityDelta) })
            .ToDictionaryAsync(x => x.VariantId, x => x.Quantity, ct);

        var stored = await _db.StockBalances
            .AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId)
            .ToListAsync(ct);

        var divergences = new List<BalanceDivergence>();

        foreach (var balance in stored)
        {
            var ledgerQuantity = ledgerTotals.GetValueOrDefault(balance.VariantId, 0m);

            if (balance.QuantityOnHand != ledgerQuantity)
            {
                divergences.Add(new BalanceDivergence(
                    warehouseId,
                    balance.VariantId,
                    balance.QuantityOnHand,
                    ledgerQuantity,
                    balance.TotalValue,
                    balance.AverageUnitCost * ledgerQuantity));
            }
        }

        // The reverse direction matters too: a variant with movements but no balance row
        // is invisible to every stock report, which is worse than a wrong number because
        // nobody goes looking for it.
        var storedVariantIds = stored.Select(b => b.VariantId).ToHashSet();

        foreach (var (variantId, quantity) in ledgerTotals.Where(t => !storedVariantIds.Contains(t.Key)))
        {
            // XXX is ISO 4217's code for "no currency", which is the honest answer here:
            // there is no balance row, so there is no currency to report. Using
            // default(Money) instead would produce an uninitialised value that throws the
            // moment anything totals it.
            var noCurrency = Money.Zero("XXX");

            divergences.Add(new BalanceDivergence(
                warehouseId, variantId, 0m, quantity, noCurrency, noCurrency));
        }

        return divergences;
    }
}
