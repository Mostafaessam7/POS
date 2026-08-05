using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POS.Contracts.Inventory;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.SharedKernel;

namespace POS.Inventory.Integration;

/// <summary>
/// Translates <see cref="IStockPostingPort"/> requests into ledger movements.
/// </summary>
/// <remarks>
/// THE ANTI-CORRUPTION LAYER. Everything Inventory-specific — movement types, negative
/// stock policy, the document reference shape — is decided here, so the calling module
/// never learns any of it. Renaming a <see cref="MovementType"/> member changes this
/// file and nothing else.
/// </remarks>
public sealed class StockPostingAdapter(
    InventoryDbContext db,
    IStockLedger ledger,
    ILogger<StockPostingAdapter> logger) : IStockPostingPort
{
    public async Task<Result> PostAsync(
        StockPostingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Lines.Count == 0)
            return Result.Success();

        // THE IDEMPOTENCY GUARD, and the reason it is a query rather than an upsert:
        // the ledger is append-only (ADR 008), so a duplicate movement can never be
        // corrected away afterwards — it can only be offset by a compensating entry,
        // which leaves two wrong-looking rows in the audit trail forever. Checking
        // first is the only cheap point at which a replay can be stopped.
        //
        // This is the query IX_StockMovements_Document exists for.
        var alreadyPosted = await db.StockMovements
            .AnyAsync(m => m.Reference.DocumentId == request.DocumentId, cancellationToken);

        if (alreadyPosted)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Stock for document {DocumentNumber} ({DocumentId}) is already posted; ignoring replay.",
                    request.DocumentNumber,
                    request.DocumentId);
            }

            return Result.Success();
        }

        var reference = new StockDocumentReference(
            DocumentTypeFor(request.Kind),
            request.DocumentId,
            request.DocumentNumber);

        var movements = new List<StockMovement>(request.Lines.Count);

        foreach (var line in request.Lines)
        {
            // The sign is applied HERE. The contract carries positive quantities and a
            // kind, precisely so no caller has to remember which way round the minus
            // goes — an inbound movement recorded as outbound is a stock error that
            // surfaces at the next count, months later.
            var quantityDelta = IsInbound(request.Kind) ? line.Quantity : -line.Quantity;

            movements.Add(StockMovement.Record(
                db.CurrentTenantId,
                request.WarehouseId,
                line.VariantId,
                MovementTypeFor(request.Kind),
                quantityDelta,
                line.UnitCost,
                reference,
                request.OccurredAt,
                request.BusinessDate,
                terminalId: null,
                userId: request.UserId));
        }

        return await ledger.RecordBatchAsync(movements, PolicyFor(request.Kind), cancellationToken);
    }

    private static bool IsInbound(StockPostingKind kind) =>
        kind is StockPostingKind.PurchaseReceipt or StockPostingKind.CustomerReturn;

    /// <summary>
    /// How hard to push back when a movement would drive stock negative (ADR 027).
    /// </summary>
    /// <remarks>
    /// Never Block. Every kind this port handles describes something that has ALREADY
    /// HAPPENED physically — goods delivered, returned to a supplier, or sold and
    /// carried out of the shop. Refusing to record it would not undo the movement, it
    /// would only make the ledger disagree with the shelf and hide the discrepancy from
    /// the exception report that exists to surface it.
    ///
    /// Sales get Allow rather than Warn because a negative balance there is routine
    /// rather than notable: an offline terminal legitimately sells stock the server has
    /// not yet heard about.
    /// </remarks>
    private static NegativeStockPolicy PolicyFor(StockPostingKind kind) => kind switch
    {
        StockPostingKind.Sale => NegativeStockPolicy.Allow,
        _ => NegativeStockPolicy.Warn
    };

    private static MovementType MovementTypeFor(StockPostingKind kind) => kind switch
    {
        StockPostingKind.PurchaseReceipt => MovementType.Receipt,
        StockPostingKind.SupplierReturn => MovementType.SupplierReturn,
        StockPostingKind.Sale => MovementType.Sale,
        StockPostingKind.CustomerReturn => MovementType.CustomerReturn,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped stock posting kind.")
    };

    private static StockDocumentType DocumentTypeFor(StockPostingKind kind) => kind switch
    {
        StockPostingKind.PurchaseReceipt => StockDocumentType.PurchaseReceipt,
        StockPostingKind.SupplierReturn => StockDocumentType.SupplierReturn,
        StockPostingKind.Sale => StockDocumentType.Sale,
        StockPostingKind.CustomerReturn => StockDocumentType.Return,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped stock posting kind.")
    };
}
