using Microsoft.EntityFrameworkCore;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Stocktakes;

/// <summary>
/// Drives a <see cref="Stocktake"/> through its lifecycle and posts the variance it finds
/// to the ledger.
/// </summary>
/// <remarks>
/// <see cref="Stocktake.Post"/> deliberately hands back adjustment instructions instead of
/// writing them (see its remarks), so this service exists to do exactly what
/// <c>GoodsReceiptPostingService</c> does for a receipt: post the ledger effect first,
/// save the aggregate's own status change second, so a crash between the two is
/// detectable and self-healing on retry rather than silently leaving the count's variance
/// unposted. Talks to <see cref="IStockLedger"/> directly rather than through
/// <see cref="POS.Contracts.Inventory.IStockPostingPort"/> because <see cref="Stocktake"/>
/// already lives inside this module — the port exists for other modules, not this one.
/// </remarks>
public sealed class StocktakeService(InventoryDbContext db, IStockLedger ledger, IClock clock)
{
    private const string VarianceReasonCode = "STOCKTAKE_VARIANCE";

    public async Task<Result<Stocktake>> StartAsync(StartStocktakeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stocktake = Stocktake.Start(
            db.CurrentTenantId,
            request.WarehouseId,
            request.StocktakeNumber,
            request.Scope,
            request.IsBlind,
            request.StartedByUserId,
            clock.UtcNow,
            request.BusinessDate);

        db.Stocktakes.Add(stocktake);
        await db.SaveChangesAsync(ct);

        return Result<Stocktake>.Success(stocktake);
    }

    /// <summary>
    /// Records a count. The expected quantity is resolved here, from the current balance,
    /// rather than trusted from the caller — a blind count must not be told the number it
    /// is not supposed to see, and a visible count must not be able to lie about it.
    /// </summary>
    public async Task<Result<Stocktake>> RecordCountAsync(
        Guid stocktakeId, Guid variantId, decimal countedQuantity, Guid userId, CancellationToken ct = default)
    {
        var stocktake = await db.Stocktakes
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == stocktakeId, ct);

        if (stocktake is null)
            return Result<Stocktake>.Failure(InventoryErrors.StocktakeNotFound);

        var balance = await db.StockBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.WarehouseId == stocktake.WarehouseId && b.VariantId == variantId, ct);

        var expected = balance?.QuantityOnHand ?? 0m;
        var isNewLine = stocktake.Lines.All(l => l.VariantId != variantId);

        var recorded = stocktake.RecordCount(variantId, countedQuantity, expected, userId, clock.UtcNow);
        if (recorded.IsFailure)
            return Result<Stocktake>.Failure(recorded.Error);

        if (isNewLine)
        {
            // A new line added to an AGGREGATE THAT WAS ALREADY LOADED is reachable only
            // through change detection on a tracked graph, not through an explicit Add()
            // call — and EF's default heuristic for a reachable entity with a
            // client-generated (non-default) key it has never seen before is Modified,
            // not Added, because it cannot tell "new" from "already exists, key assigned
            // by the client". Left uncorrected this produces an UPDATE against a row that
            // does not exist yet, which SQL Server reports as a concurrency conflict
            // (0 rows affected) rather than the missing-INSERT it actually is.
            var line = stocktake.Lines.Single(l => l.VariantId == variantId);
            db.Entry(line).State = EntityState.Added;
        }

        await db.SaveChangesAsync(ct);
        return Result<Stocktake>.Success(stocktake);
    }

    public async Task<Result<Stocktake>> CompleteCountingAsync(Guid stocktakeId, CancellationToken ct = default)
    {
        var stocktake = await db.Stocktakes
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == stocktakeId, ct);

        if (stocktake is null)
            return Result<Stocktake>.Failure(InventoryErrors.StocktakeNotFound);

        var completed = stocktake.CompleteCounting();
        if (completed.IsFailure)
            return Result<Stocktake>.Failure(completed.Error);

        await db.SaveChangesAsync(ct);
        return Result<Stocktake>.Success(stocktake);
    }

    public async Task<Result<Stocktake>> PostAsync(Guid stocktakeId, Guid userId, CancellationToken ct = default)
    {
        var stocktake = await db.Stocktakes
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == stocktakeId, ct);

        if (stocktake is null)
            return Result<Stocktake>.Failure(InventoryErrors.StocktakeNotFound);

        if (stocktake.Status == StocktakeStatus.Posted)
        {
            // Already posted — the retry-after-crash no-op, same idempotency stance as
            // the transfer service.
            return Result<Stocktake>.Success(stocktake);
        }

        var alreadyPosted = await db.StockMovements.AnyAsync(
            m => m.Reference.DocumentType == StockDocumentType.Stocktake && m.Reference.DocumentId == stocktakeId,
            ct);

        var posting = stocktake.Post(userId, clock.UtcNow);
        if (posting.IsFailure)
            return Result<Stocktake>.Failure(posting.Error);

        if (!alreadyPosted && posting.Value.Count > 0)
        {
            var costs = await LoadCostsAsync(stocktake.WarehouseId, posting.Value.Select(a => a.VariantId), ct);

            var now = clock.UtcNow;
            var businessDate = stocktake.BusinessDate;
            var reference = new StockDocumentReference(StockDocumentType.Stocktake, stocktakeId, stocktake.StocktakeNumber);

            var movements = new List<StockMovement>();

            foreach (var adjustment in posting.Value)
            {
                if (!costs.TryGetValue(adjustment.VariantId, out var cost))
                    return Result<Stocktake>.Failure(InventoryErrors.UnknownVariant);

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, stocktake.WarehouseId, adjustment.VariantId,
                    MovementType.StocktakeAdjustment, adjustment.QuantityDelta, cost, reference, now, businessDate,
                    terminalId: null, userId: userId, reasonCode: VarianceReasonCode));
            }

            var posted = await ledger.RecordBatchAsync(movements, NegativeStockPolicy.Warn, ct);
            if (posted.IsFailure)
                return Result<Stocktake>.Failure(posted.Error);
        }

        await db.SaveChangesAsync(ct);
        return Result<Stocktake>.Success(stocktake);
    }

    /// <summary>Abandons a count before it has been posted to the ledger.</summary>
    public async Task<Result<Stocktake>> CancelAsync(
        Guid stocktakeId, string reason, Guid userId, CancellationToken ct = default)
    {
        var stocktake = await db.Stocktakes
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == stocktakeId, ct);

        if (stocktake is null)
            return Result<Stocktake>.Failure(InventoryErrors.StocktakeNotFound);

        var cancelled = stocktake.Cancel(reason, userId, clock.UtcNow);
        if (cancelled.IsFailure)
            return Result<Stocktake>.Failure(cancelled.Error);

        await db.SaveChangesAsync(ct);
        return Result<Stocktake>.Success(stocktake);
    }

    /// <remarks>
    /// A variant with no balance row in this warehouse has no prevailing cost to consume
    /// or blend at, so it is left out of the map and the caller surfaces
    /// <see cref="InventoryErrors.UnknownVariant"/> for that line — the same stance
    /// <c>StockAdjustmentService</c> takes, rather than inventing a cost or a currency.
    /// </remarks>
    private async Task<Dictionary<Guid, Money>> LoadCostsAsync(
        Guid warehouseId, IEnumerable<Guid> variantIds, CancellationToken ct)
    {
        var ids = variantIds.Distinct().ToList();

        return await db.StockBalances
            .AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId && ids.Contains(b.VariantId))
            .ToDictionaryAsync(b => b.VariantId, b => b.AverageUnitCost, ct);
    }
}

public sealed record StartStocktakeRequest(
    Guid WarehouseId,
    string StocktakeNumber,
    StocktakeScope Scope,
    bool IsBlind,
    Guid StartedByUserId,
    DateOnly BusinessDate);
