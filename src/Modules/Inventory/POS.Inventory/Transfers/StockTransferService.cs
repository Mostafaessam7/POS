using Microsoft.EntityFrameworkCore;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Transfers;

/// <summary>
/// Drives a <see cref="StockTransfer"/> through its lifecycle and posts the stock effect
/// of each leg to the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Same ordering discipline as <c>GoodsReceiptPostingService</c> (see its remarks for the
/// full rationale), applied intra-module instead of across the Purchasing/Inventory
/// boundary: for each state transition that moves stock, the ledger write commits FIRST
/// and the transfer's own status change is saved SECOND. A crash between the two leaves
/// stock moved and the transfer not yet advanced — safe to retry, because the retry
/// re-derives the identical movements and the idempotency check below recognises them as
/// already posted.
/// </para>
/// <para>
/// <see cref="POS.Contracts.Inventory.IStockPostingPort"/> is not used here on purpose. That port is the
/// anti-corruption seam for OTHER modules to affect stock without depending on
/// <c>POS.Inventory.Domain</c> directly; <see cref="StockTransfer"/> already lives inside
/// this module, so this service talks to <see cref="IStockLedger"/> directly, the same
/// way <c>StockAdjustmentService</c> does.
/// </para>
/// <para>
/// Idempotency is keyed on (document id, movement type, warehouse) rather than document
/// id alone, because a single transfer produces movements at up to three different
/// warehouses across its lifecycle (dispatch, receive, write-off) and each leg must be
/// individually replayable.
/// </para>
/// </remarks>
public sealed class StockTransferService(InventoryDbContext db, IStockLedger ledger, IClock clock)
{
    public async Task<Result<StockTransfer>> CreateAsync(CreateStockTransferRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        StockTransfer transfer;
        try
        {
            transfer = StockTransfer.Draft(
                db.CurrentTenantId,
                request.SourceWarehouseId,
                request.DestinationWarehouseId,
                request.InTransitWarehouseId,
                request.TransferNumber,
                request.CreatedByUserId,
                clock.UtcNow);
        }
        catch (ArgumentException ex)
        {
            return Result<StockTransfer>.Failure(Error.Validation("inventory.transfer_invalid", ex.Message));
        }

        foreach (var line in request.Lines)
            transfer.AddLine(line.VariantId, line.Quantity);

        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync(ct);

        return Result<StockTransfer>.Success(transfer);
    }

    /// <summary>Moves the sent quantities from the source warehouse into in-transit.</summary>
    public async Task<Result<StockTransfer>> DispatchAsync(Guid transferId, Guid userId, CancellationToken ct = default)
    {
        var transfer = await db.StockTransfers
            .Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (transfer is null)
            return Result<StockTransfer>.Failure(InventoryErrors.TransferNotFound);

        if (transfer.Status != TransferStatus.Draft)
        {
            // Already dispatched — the aggregate's own state is the idempotency guard
            // for a caller that retries after the ledger committed but the save did not
            // reach disk. Nothing left to do.
            return transfer.Status == TransferStatus.InTransit
                || transfer.Status is TransferStatus.ReceivedWithVariance or TransferStatus.Completed
                ? Result<StockTransfer>.Success(transfer)
                : Result<StockTransfer>.Failure(InventoryErrors.TransferNotDraft);
        }

        var alreadyPosted = await db.StockMovements.AnyAsync(
            m => m.Reference.DocumentType == StockDocumentType.Transfer
                && m.Reference.DocumentId == transferId
                && m.Type == MovementType.TransferOut
                && m.WarehouseId == transfer.SourceWarehouseId,
            ct);

        if (!alreadyPosted)
        {
            var costs = await LoadCostsAsync(transfer.SourceWarehouseId, transfer.Lines.Select(l => l.VariantId), ct);

            var movements = new List<StockMovement>();
            var now = clock.UtcNow;
            var businessDate = DateOnly.FromDateTime(now.UtcDateTime);
            var reference = new StockDocumentReference(StockDocumentType.Transfer, transferId, transfer.TransferNumber);

            foreach (var line in transfer.Lines)
            {
                if (!costs.TryGetValue(line.VariantId, out var cost))
                    return Result<StockTransfer>.Failure(InventoryErrors.UnknownVariant);

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, transfer.SourceWarehouseId, line.VariantId,
                    MovementType.TransferOut, -line.QuantitySent, cost, reference, now, businessDate,
                    terminalId: null, userId: userId));

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, transfer.InTransitWarehouseId, line.VariantId,
                    MovementType.TransferIn, line.QuantitySent, cost, reference, now, businessDate,
                    terminalId: null, userId: userId));
            }

            var posted = await ledger.RecordBatchAsync(movements, NegativeStockPolicy.Warn, ct);
            if (posted.IsFailure)
                return Result<StockTransfer>.Failure(posted.Error);
        }

        var dispatched = transfer.Dispatch(userId, clock.UtcNow);
        if (dispatched.IsFailure)
            return Result<StockTransfer>.Failure(dispatched.Error);

        await db.SaveChangesAsync(ct);
        return Result<StockTransfer>.Success(transfer);
    }

    /// <summary>Moves what actually arrived from in-transit into the destination.</summary>
    public async Task<Result<StockTransfer>> ReceiveAsync(
        Guid transferId,
        IReadOnlyDictionary<Guid, decimal> receivedQuantities,
        Guid userId,
        CancellationToken ct = default)
    {
        var transfer = await db.StockTransfers
            .Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (transfer is null)
            return Result<StockTransfer>.Failure(InventoryErrors.TransferNotFound);

        if (transfer.Status is TransferStatus.ReceivedWithVariance or TransferStatus.Completed)
        {
            // Already received — same retry-after-crash reasoning as dispatch.
            return Result<StockTransfer>.Success(transfer);
        }

        var alreadyPosted = await db.StockMovements.AnyAsync(
            m => m.Reference.DocumentType == StockDocumentType.Transfer
                && m.Reference.DocumentId == transferId
                && m.Type == MovementType.TransferIn
                && m.WarehouseId == transfer.DestinationWarehouseId,
            ct);

        // The domain records what arrived on every line, so the received quantities must
        // be resolved before movements are built. Doing this against a snapshot of the
        // dictionary is safe: Receive() is only reachable from InTransit, which the
        // aggregate itself will refuse to leave twice.
        var received = transfer.Status == TransferStatus.InTransit
            ? transfer.Receive(receivedQuantities, userId, clock.UtcNow)
            : Result.Failure(InventoryErrors.TransferNotInTransit);

        if (received.IsFailure)
            return Result<StockTransfer>.Failure(received.Error);

        if (!alreadyPosted)
        {
            var costs = await LoadCostsAsync(transfer.InTransitWarehouseId, transfer.Lines.Select(l => l.VariantId), ct);

            var movements = new List<StockMovement>();
            var now = clock.UtcNow;
            var businessDate = DateOnly.FromDateTime(now.UtcDateTime);
            var reference = new StockDocumentReference(StockDocumentType.Transfer, transferId, transfer.TransferNumber);

            foreach (var line in transfer.Lines)
            {
                var quantity = line.QuantityReceived ?? 0m;
                if (quantity <= 0m)
                    continue;

                if (!costs.TryGetValue(line.VariantId, out var cost))
                    return Result<StockTransfer>.Failure(InventoryErrors.UnknownVariant);

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, transfer.InTransitWarehouseId, line.VariantId,
                    MovementType.TransferOut, -quantity, cost, reference, now, businessDate,
                    terminalId: null, userId: userId));

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, transfer.DestinationWarehouseId, line.VariantId,
                    MovementType.TransferIn, quantity, cost, reference, now, businessDate,
                    terminalId: null, userId: userId));
            }

            if (movements.Count > 0)
            {
                var posted = await ledger.RecordBatchAsync(movements, NegativeStockPolicy.Warn, ct);
                if (posted.IsFailure)
                    return Result<StockTransfer>.Failure(posted.Error);
            }
        }

        await db.SaveChangesAsync(ct);
        return Result<StockTransfer>.Success(transfer);
    }

    /// <summary>Disposes of an unresolved variance still sitting in the in-transit warehouse.</summary>
    public async Task<Result<StockTransfer>> WriteOffVarianceAsync(
        Guid transferId,
        InventoryPolicyOptions policy,
        Guid userId,
        ApprovalLevel approverLevel,
        string reasonCode,
        CancellationToken ct = default)
    {
        var transfer = await db.StockTransfers
            .Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (transfer is null)
            return Result<StockTransfer>.Failure(InventoryErrors.TransferNotFound);

        if (transfer.Status == TransferStatus.Completed && transfer.VarianceWrittenOffAt is not null)
        {
            // Already written off — retry-after-crash no-op.
            return Result<StockTransfer>.Success(transfer);
        }

        var varianceLines = transfer.Lines.Where(l => l.HasVariance).ToList();

        // Costs are loaded BEFORE the domain call, not after, because the approval
        // ladder needs the variance's monetary value to pick the required level — the
        // aggregate cannot compute that itself, since a transfer line only ever holds
        // quantities, never cost (cost is resolved from the prevailing balance at
        // posting time, same as every other movement in this service).
        var costs = await LoadCostsAsync(transfer.InTransitWarehouseId, varianceLines.Select(l => l.VariantId), ct);

        Money? varianceValue = null;
        foreach (var line in varianceLines)
        {
            if (!costs.TryGetValue(line.VariantId, out var cost))
                return Result<StockTransfer>.Failure(InventoryErrors.UnknownVariant);

            var lineValue = cost * Math.Abs(line.Variance);
            varianceValue = varianceValue is null ? lineValue : varianceValue.Value + lineValue;
        }

        // varianceLines is non-empty whenever Status is ReceivedWithVariance (that
        // status is only reached when HasVariance was true, and lines don't change
        // between receiving and writing off), so the loop above always runs at least
        // once by the time WriteOffVariance's own status check would otherwise fail.
        var value = varianceValue ?? Money.Zero("USD");

        var writtenOff = transfer.WriteOffVariance(
            policy.VarianceApprovalPolicyFor(value.Currency), value, userId, approverLevel, reasonCode, clock.UtcNow);

        if (writtenOff.IsFailure)
            return Result<StockTransfer>.Failure(writtenOff.Error);

        var alreadyPosted = await db.StockMovements.AnyAsync(
            m => m.Reference.DocumentType == StockDocumentType.Transfer
                && m.Reference.DocumentId == transferId
                && (m.Type == MovementType.Wastage || m.Type == MovementType.AdjustmentIncrease)
                && m.WarehouseId == transfer.InTransitWarehouseId,
            ct);

        if (!alreadyPosted && varianceLines.Count > 0)
        {
            var movements = new List<StockMovement>();
            var now = clock.UtcNow;
            var businessDate = DateOnly.FromDateTime(now.UtcDateTime);
            var reference = new StockDocumentReference(StockDocumentType.Transfer, transferId, transfer.TransferNumber);

            foreach (var line in varianceLines)
            {
                var cost = costs[line.VariantId];

                // A shortfall (negative variance) is a write-off; a positive variance —
                // more arrived at the in-transit leg than was ever dispatched, a rare
                // miscount rather than the theft case this control targets — is recorded
                // as a found-stock increase instead, so either direction is accounted for
                // rather than only the shortfall the ADR names explicitly.
                var type = line.Variance < 0m ? MovementType.Wastage : MovementType.AdjustmentIncrease;

                movements.Add(StockMovement.Record(
                    db.CurrentTenantId, transfer.InTransitWarehouseId, line.VariantId,
                    type, line.Variance, cost, reference, now, businessDate,
                    terminalId: null, userId: userId, reasonCode: reasonCode));
            }

            var posted = await ledger.RecordBatchAsync(movements, NegativeStockPolicy.Warn, ct);
            if (posted.IsFailure)
                return Result<StockTransfer>.Failure(posted.Error);
        }

        await db.SaveChangesAsync(ct);
        return Result<StockTransfer>.Success(transfer);
    }

    /// <summary>Abandons a transfer that has not been dispatched yet.</summary>
    public async Task<Result<StockTransfer>> CancelAsync(
        Guid transferId, string reason, Guid userId, CancellationToken ct = default)
    {
        var transfer = await db.StockTransfers
            .Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (transfer is null)
            return Result<StockTransfer>.Failure(InventoryErrors.TransferNotFound);

        var cancelled = transfer.Cancel(reason, userId, clock.UtcNow);
        if (cancelled.IsFailure)
            return Result<StockTransfer>.Failure(cancelled.Error);

        await db.SaveChangesAsync(ct);
        return Result<StockTransfer>.Success(transfer);
    }

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

public sealed record CreateStockTransferRequest(
    Guid SourceWarehouseId,
    Guid DestinationWarehouseId,
    Guid InTransitWarehouseId,
    string TransferNumber,
    Guid CreatedByUserId,
    IReadOnlyList<CreateStockTransferLine> Lines);

public sealed record CreateStockTransferLine(Guid VariantId, decimal Quantity);
