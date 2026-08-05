using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Inventory.Domain;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Ledger;

/// <summary>
/// SQL Server implementation. Splits movements by whether they can change cost.
/// </summary>
/// <remarks>
/// <para>
/// The central performance decision of Phase 4 (ADR 026). Six terminals selling the
/// same bottle of water must all decrement one balance row. Read-modify-write loses
/// updates; optimistic retry degrades under exactly the contention it needs to handle;
/// pessimistic locking makes the best-selling product the throughput ceiling.
/// </para>
/// <para>
/// The way out is that quantity and cost have different arithmetic. Quantity is
/// associative, so <c>SET Quantity = Quantity - @n</c> is atomic in the database with
/// no read, no version check and no retry. Weighted average cost is not associative and
/// needs a lock — but only inbound movements change it, and inbound movements are
/// low-frequency back-office actions. The contended path is therefore never on the
/// checkout critical path.
/// </para>
/// </remarks>
public sealed class SqlServerStockLedger : IStockLedger
{
    private readonly InventoryDbContext _db;

    public SqlServerStockLedger(InventoryDbContext db) => _db = db;

    // No ITenantContext dependency by design. Tenancy is already enforced twice over:
    // the global query filter scopes every read, and TenantGuardInterceptor rejects any
    // write carrying a foreign TenantId (ADR 006). Injecting a third check here would
    // duplicate the boundary rather than strengthen it, and the raw-SQL statements below
    // carry TenantId explicitly in their predicates for exactly that reason.

    public Task<Result> RecordAsync(StockMovement movement, NegativeStockPolicy policy, CancellationToken ct = default)
        => RecordBatchAsync([movement], policy, ct);

    public async Task<Result> RecordBatchAsync(
        IReadOnlyCollection<StockMovement> movements,
        NegativeStockPolicy policy,
        CancellationToken ct = default)
    {
        if (movements.Count == 0)
        {
            return Result.Success();
        }

        // THE WHOLE UNIT GOES THROUGH THE EXECUTION STRATEGY, and it is not optional.
        // PosSqlServer enables retry-on-failure because Azure SQL drops connections
        // routinely; a retrying strategy then refuses a user-initiated transaction
        // outright, because retrying half of one would re-apply some statements and not
        // others. Handing it the entire transaction as the retriable unit is the
        // supported way to keep both.
        //
        // The strategy re-invokes this delegate from the start on a transient failure,
        // which is safe: nothing here is committed until the very end, so a retry begins
        // from a clean transaction rather than a half-applied one.
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this delegate with the previous attempt's movements and
            // balance edits still tracked. Clearing makes the unit genuinely repeatable
            // rather than accumulating a second copy of every row.
            //
            // ONLY on a retry. Clearing on the first attempt would silently discard
            // whatever the caller had pending on this context — work the SaveChanges at
            // the end of this method would otherwise have committed.
            if (attempt++ > 0)
                _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // The ledger insert happens first and unconditionally. It is the authority,
            // and it must be durable even if the balance projection subsequently needs
            // repair.
            _db.StockMovements.AddRange(movements);

            // Every balance row the batch will touch is created up front, idempotently.
            // Doing this here rather than lazily inside the loop removes the create-race
            // from the hot path entirely: after this call every UPDATE below is
            // guaranteed to find its row, so the "affected == 0" case cannot arise
            // mid-batch.
            await EnsureBalanceRowsExistAsync(movements, ct);

            foreach (var movement in movements)
            {
                var result = movement.Type.AffectsAverageCost()
                    ? await ApplyCostChangingAsync(movement, ct)
                    : await ApplyQuantityOnlyAsync(movement, policy, ct);

                if (result.IsFailure)
                {
                    await tx.RollbackAsync(ct);
                    return result;
                }
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success();
        });
    }

    /// <summary>
    /// Creates any missing balance rows for the batch, tolerating concurrent creation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two terminals selling a variant that has never moved in this warehouse will both
    /// find no row and both try to create one. The unique index on
    /// (TenantId, WarehouseId, VariantId) guarantees exactly one wins.
    /// </para>
    /// <para>
    /// The loser's failure is EXPECTED, not exceptional: the row it wanted now exists,
    /// which is the outcome it was after. Swallowing the violation and continuing is
    /// therefore correct, and is the same idempotency stance the sync ingest takes
    /// (ADR 018) — a uniqueness violation on insert means "already done", not "failed".
    /// </para>
    /// <para>
    /// The insert is guarded by NOT EXISTS as well, so in the overwhelmingly common
    /// uncontended case no exception is thrown at all; the guard is the fast path and
    /// the catch is the correctness backstop.
    /// </para>
    /// </remarks>
    private async Task EnsureBalanceRowsExistAsync(
        IReadOnlyCollection<StockMovement> movements,
        CancellationToken ct)
    {
        var keys = movements
            .Select(m => (m.TenantId, m.WarehouseId, m.VariantId, m.UnitCost.Currency))
            .Distinct();

        // The seed row must be byte-for-byte what StockBalance.Empty would have
        // produced, because CreateBalanceAsync builds the same row through the
        // aggregate. LastMovementAt is non-nullable and Empty leaves it at default, so
        // this SQL has to as well — inserting NULL fails, and inserting "now" would
        // claim a movement that has not happened.
        var neverMoved = default(DateTimeOffset);

        foreach (var (tenantId, warehouseId, variantId, currency) in keys)
        {
            try
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO inventory.StockBalances
                         (Id, TenantId, WarehouseId, VariantId,
                          QuantityOnHand, AverageUnitCostAmount, AverageUnitCostCurrency,
                          TotalValueAmount, TotalValueCurrency, LastMovementAt)
                     SELECT {SequentialId.New()}, {tenantId}, {warehouseId}, {variantId},
                            0, 0, {currency}, 0, {currency}, {neverMoved}
                      WHERE NOT EXISTS (
                            SELECT 1 FROM inventory.StockBalances
                             WHERE TenantId = {tenantId}
                               AND WarehouseId = {warehouseId}
                               AND VariantId = {variantId})
                     """, ct);
            }
            catch (Exception ex) when (UniqueViolation.Matches(ex))
            {
                // Lost the race. The row exists, which is all we needed.
                //
                // Catching Exception-with-filter rather than DbUpdateException is
                // deliberate: ExecuteSqlInterpolatedAsync does not wrap, so the
                // provider's SqlException arrives raw and a DbUpdateException catch
                // never fires. See UniqueViolation.
            }
        }
    }

    /// <summary>
    /// The hot path. One atomic relative UPDATE, no read, no lock contention beyond the
    /// row latch.
    /// </summary>
    private async Task<Result> ApplyQuantityOnlyAsync(
        StockMovement movement,
        NegativeStockPolicy policy,
        CancellationToken ct)
    {
        // Block policy is the only case that must read first, because it needs to know
        // the current value to reject. That read is the price of the policy, and it is
        // why Block is not the default — see ADR 027.
        if (policy == NegativeStockPolicy.Block)
        {
            var current = await _db.StockBalances
                .Where(b => b.WarehouseId == movement.WarehouseId && b.VariantId == movement.VariantId)
                .Select(b => (decimal?)b.QuantityOnHand)
                .FirstOrDefaultAsync(ct);

            if ((current ?? 0m) + movement.QuantityDelta < 0m)
            {
                return Result.Failure(InventoryErrors.InsufficientStock);
            }
        }

        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE inventory.StockBalances
                SET QuantityOnHand = QuantityOnHand + {movement.QuantityDelta},
                    TotalValueAmount = AverageUnitCostAmount * (QuantityOnHand + {movement.QuantityDelta}),
                    LastMovementAt = {movement.OccurredAt}
              WHERE TenantId    = {movement.TenantId}
                AND WarehouseId = {movement.WarehouseId}
                AND VariantId   = {movement.VariantId}
             """, ct);

        if (affected == 0)
        {
            // Unreachable in normal operation: EnsureBalanceRowsExistAsync guarantees
            // the row. Reaching here means the row was deleted underneath us, which
            // would violate the append-only contract, so it is a hard failure rather
            // than something to paper over by recreating the row.
            return Result.Failure(InventoryErrors.BalanceRowMissing);
        }

        return Result.Success();
    }

    /// <summary>
    /// The cold path. Takes a row lock because the new average depends on the current one.
    /// </summary>
    private async Task<Result> ApplyCostChangingAsync(StockMovement movement, CancellationToken ct)
    {
        // UPDLOCK acquires the update lock immediately rather than escalating from a
        // shared lock, which is what causes deadlocks when two receipts race. HOLDLOCK
        // keeps it to the end of the transaction so no one slips in between the read and
        // the write.
        //
        // THE LOCK IS TAKEN SEPARATELY FROM THE READ, which looks redundant and is not.
        // The obvious form — FromSql("SELECT * ... WITH (UPDLOCK, HOLDLOCK)") — does not
        // work here: EF composes a projection over the raw SQL as a subquery, and for
        // COMPLEX PROPERTIES it falls back to default column naming (AverageUnitCost_Amount)
        // instead of the names the mapping configured (AverageUnitCostAmount). The query
        // then fails at runtime against a schema that is perfectly correct.
        //
        // Acquiring the lock with a statement that projects nothing, then reading through
        // the normal LINQ path, keeps both properties: the row is locked for the rest of
        // the transaction, and the entity materialises through the real mapping.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             SELECT TOP 1 1 FROM inventory.StockBalances WITH (UPDLOCK, HOLDLOCK)
              WHERE TenantId    = {movement.TenantId}
                AND WarehouseId = {movement.WarehouseId}
                AND VariantId   = {movement.VariantId}
             """, ct);

        var balance = await _db.StockBalances
            .FirstOrDefaultAsync(
                b => b.WarehouseId == movement.WarehouseId && b.VariantId == movement.VariantId,
                ct);

        if (balance is null)
        {
            await CreateBalanceAsync(movement, ct);
            return Result.Success();
        }

        balance.ApplyInbound(movement.QuantityDelta, movement.UnitCost, movement.OccurredAt);
        return Result.Success();
    }

    private async Task CreateBalanceAsync(StockMovement movement, CancellationToken ct)
    {
        var balance = StockBalance.Empty(
            movement.TenantId,
            movement.WarehouseId,
            movement.VariantId,
            movement.UnitCost.Currency);

        if (movement.IsInbound)
        {
            balance.ApplyInbound(movement.QuantityDelta, movement.UnitCost, movement.OccurredAt);
        }
        else
        {
            balance.Revalue(movement.UnitCost, movement.OccurredAt);
            balance.ApplyOutbound(Math.Abs(movement.QuantityDelta), movement.OccurredAt);
        }

        _db.StockBalances.Add(balance);

        // A concurrent creation of the same balance row is possible and is handled by the
        // unique index on (TenantId, WarehouseId, VariantId): the loser retries into the
        // UPDATE path above. Treating the constraint violation as retryable rather than
        // fatal is the same idempotency stance the sync ingest takes (ADR 018).
        await Task.CompletedTask;
    }

    public async Task<StockBalance?> GetBalanceAsync(Guid warehouseId, Guid variantId, CancellationToken ct = default)
        => await _db.StockBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.WarehouseId == warehouseId && b.VariantId == variantId, ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetOnHandAsync(
        Guid warehouseId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken ct = default)
        => await _db.StockBalances
            .AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId && variantIds.Contains(b.VariantId))
            .ToDictionaryAsync(b => b.VariantId, b => b.QuantityOnHand, ct);
}
