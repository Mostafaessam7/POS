using Microsoft.EntityFrameworkCore;
using POS.Contracts.Inventory;
using POS.Purchasing.Domain;
using POS.Purchasing.Persistence;
using POS.SharedKernel;

namespace POS.Purchasing.Posting;

/// <summary>
/// Posts a goods receipt and moves the stock it represents.
/// </summary>
/// <remarks>
/// <para>
/// THE ORDER OF THE TWO WRITES IS THE WHOLE DESIGN, so it is spelled out rather than
/// left to be inferred:
/// </para>
/// <list type="number">
///   <item>Ask the aggregate to post. This validates against the order and its
///   tolerance, allocates landed cost across the lines, and produces the stock
///   instructions. Nothing is saved.</item>
///   <item>Move the stock, through the Inventory port. This commits.</item>
///   <item>Save the receipt as posted. This commits.</item>
/// </list>
/// <para>
/// Reversing steps 2 and 3 is the natural way to write it — save the local record
/// first, then tell the other module — and it is wrong. A crash between them would
/// leave a receipt marked Posted whose stock never moved: the shelf and the system
/// disagree, no query detects it, and it surfaces weeks later as an unexplained
/// shortfall at a stock count.
/// </para>
/// <para>
/// This way round, the same crash leaves stock moved and the receipt not yet posted,
/// which is BOTH detectable and self-healing. Posting again re-derives the identical
/// instructions; the port recognises the document and applies nothing; the receipt then
/// saves. At-least-once delivery plus an idempotent consumer, which is the same bargain
/// the sync ingest path makes and for the same reason — there is no distributed
/// transaction across two module contexts and inventing one would be worse than
/// converging.
/// </para>
/// </remarks>
public sealed class GoodsReceiptPostingService(
    PurchasingDbContext db,
    IStockPostingPort stock,
    IClock clock)
{
    public async Task<Result<GoodsReceiptPosting>> PostAsync(
        Guid goodsReceiptId,
        ReceiptTolerance tolerance,
        CancellationToken cancellationToken = default)
    {
        var receipt = await db.GoodsReceipts
            .Include(r => r.Lines)
            .Include(r => r.LandedCosts)
            .FirstOrDefaultAsync(r => r.Id == goodsReceiptId, cancellationToken);

        if (receipt is null)
            return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.ReceiptNotFound);

        var order = await db.PurchaseOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == receipt.PurchaseOrderId, cancellationToken);

        if (order is null)
            return Result<GoodsReceiptPosting>.Failure(PurchasingErrors.OrderNotFound);

        var posting = receipt.Post(order, tolerance, clock.UtcNow);

        if (posting.IsFailure)
            return posting;

        var stockResult = await stock.PostAsync(
            new StockPostingRequest
            {
                WarehouseId = posting.Value.WarehouseId,
                Kind = StockPostingKind.PurchaseReceipt,
                DocumentId = posting.Value.GoodsReceiptId,
                DocumentNumber = posting.Value.ReceiptNumber,
                OccurredAt = posting.Value.PostedAt,
                BusinessDate = posting.Value.BusinessDate.Value,

                // The LANDED unit cost, not the supplier's price. Freight and duty have
                // already been apportioned across the lines by the aggregate; costing
                // the goods at the invoice price is how margin overstates itself
                // (ADR 052).
                Lines = [.. posting.Value.Movements.Select(m =>
                    new StockPostingLine(m.VariantId, m.Quantity, m.LandedUnitCost))]
            },
            cancellationToken);

        if (stockResult.IsFailure)
        {
            // Nothing is saved on this side, so the receipt stays unposted and the
            // whole operation can simply be retried. Returning the inventory error
            // unchanged keeps the real cause visible instead of flattening it into
            // "posting failed".
            return Result<GoodsReceiptPosting>.Failure(stockResult.Error);
        }

        await db.SaveChangesAsync(cancellationToken);

        return posting;
    }
}
