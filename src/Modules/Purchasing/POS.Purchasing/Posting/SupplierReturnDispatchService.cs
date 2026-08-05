using Microsoft.EntityFrameworkCore;
using POS.Contracts.Inventory;
using POS.Purchasing.Domain;
using POS.Purchasing.Persistence;
using POS.SharedKernel;

namespace POS.Purchasing.Posting;

/// <summary>
/// Dispatches a supplier return and takes the stock off the shelf.
/// </summary>
/// <remarks>
/// Same ordering as <see cref="GoodsReceiptPostingService"/>, and for the same reason:
/// stock moves before the local record is saved, so that a crash leaves a detectable,
/// self-healing state rather than a return marked Dispatched whose goods are still
/// counted as on hand.
///
/// The asymmetry worth noticing is that here the goods have PHYSICALLY LEFT by the time
/// this runs. That is why the Inventory adapter posts these under a policy that permits
/// negative stock rather than rejecting: refusing to record the movement because the
/// system's idea of stock disagrees with the shelf would leave the ledger further from
/// the truth, not closer to it (ADR 027).
/// </remarks>
public sealed class SupplierReturnDispatchService(
    PurchasingDbContext db,
    IStockPostingPort stock,
    IClock clock)
{
    public async Task<Result<SupplierReturnPosting>> DispatchAsync(
        Guid supplierReturnId,
        CancellationToken cancellationToken = default)
    {
        var supplierReturn = await db.SupplierReturns
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == supplierReturnId, cancellationToken);

        if (supplierReturn is null)
            return Result<SupplierReturnPosting>.Failure(PurchasingErrors.ReturnNotFound);

        var posting = supplierReturn.Dispatch(clock.UtcNow);

        if (posting.IsFailure)
            return posting;

        var stockResult = await stock.PostAsync(
            new StockPostingRequest
            {
                WarehouseId = posting.Value.WarehouseId,
                Kind = StockPostingKind.SupplierReturn,
                DocumentId = posting.Value.SupplierReturnId,
                DocumentNumber = posting.Value.ReturnNumber,
                OccurredAt = posting.Value.DispatchedAt,
                BusinessDate = posting.Value.BusinessDate.Value,
                Lines = [.. posting.Value.Movements.Select(m =>
                    new StockPostingLine(m.VariantId, m.Quantity, m.UnitCost))]
            },
            cancellationToken);

        if (stockResult.IsFailure)
            return Result<SupplierReturnPosting>.Failure(stockResult.Error);

        await db.SaveChangesAsync(cancellationToken);

        return posting;
    }
}
