using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using Microsoft.Extensions.Logging;
using POS.Contracts.Fiscal;
using POS.Contracts.Inventory;
using POS.Contracts.Payments;
using POS.Sales.Domain;
using POS.Sales.Persistence;
using POS.SharedKernel;
using POS.Sync.Contracts;
using POS.Sync.Ingest;

namespace POS.Sales.Sync;

/// <summary>
/// Applies an uploaded sale to the Sales module.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Sales, not in Sync, and is registered by Sales. Sync owns transport and
/// knows nothing about what a record means; if this class lived there, Sync would need
/// a project reference to every module, which architecture rule 2 forbids and the
/// architecture suite fails the build over.
/// </para>
/// <para>
/// IDEMPOTENCY IS BY BUSINESS KEY, and this is the important design point. The ingest
/// service saves its own <c>SyncedRecords</c> rows AFTER handlers have run, in a
/// separate context and therefore a separate transaction. So there is a window in
/// which a sale is persisted and the record marking it as seen is not — a crash there,
/// or a lost response, means the terminal re-uploads a sale this module has already
/// applied.
/// </para>
/// <para>
/// Rather than reach for a distributed transaction across two module contexts, this
/// handler is written to be safely re-runnable: it keys on (TerminalId, ReceiptSeries,
/// ReceiptSequence), which is already unique per terminal and backed by
/// <c>UX_Sales_Receipt</c> (ADR 005). A replay finds the existing sale and reports
/// success without writing anything. At-least-once delivery plus an idempotent handler
/// gives the same guarantee as exactly-once, which is not achievable across an
/// unreliable network anyway.
/// </para>
/// <para>
/// The unique index is the real guarantee; the pre-check only avoids doing the work
/// twice. Two concurrent uploads of the same batch both pass the check, and the loser's
/// INSERT is rejected by the constraint — which is handled here as SUCCESS, because it
/// means somebody else applied the identical sale.
/// </para>
/// </remarks>
public sealed class SaleSyncHandler(
    SalesDbContext db,
    IStockPostingPort stock,
    IFiscalisationPort fiscal,
    IPaymentRecordingPort payments,
    ILogger<SaleSyncHandler> logger) : ISyncRecordHandler
{
    /// <summary>The record type this handler claims. Matches what the terminal sends.</summary>
    public const string Type = "sale";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string RecordType => Type;

    public async Task<Result> HandleAsync(
        Guid tenantId,
        SyncRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        SaleSyncPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<SaleSyncPayload>(record.Payload, Json);
        }
        catch (JsonException ex)
        {
            // A malformed record is REJECTED, never retried. Returning a failure here
            // is what lets the twelve valid sales behind it through; failing the batch
            // would have the terminal retry forever and the store's takings would never
            // reach HQ.
            return Error.Validation("sales.sync.payload_invalid", $"Sale payload is not valid JSON: {ex.Message}");
        }

        if (payload is null)
            return Error.Validation("sales.sync.payload_empty", "Sale payload was empty.");

        var alreadyApplied = await db.Sales.AnyAsync(
            s => s.TenantId == tenantId
              && s.TerminalId == payload.TerminalId
              && s.ReceiptNumber.Series == payload.ReceiptSeries
              && s.ReceiptNumber.Sequence == payload.ReceiptSequence,
            cancellationToken);

        if (alreadyApplied)
            return Result.Success();

        var reconstituted = Reconstitute(tenantId, payload);

        if (reconstituted.IsFailure)
            return Result.Failure(reconstituted.Error);

        var sale = reconstituted.Value;

        // STOCK MOVES BEFORE THE SALE IS SAVED, and the ordering is the same bargain
        // the purchasing posting services make. Saving the sale first and then moving
        // stock would mean a crash between the two leaves a sale that everyone can see
        // and stock that never left the shelf — and the replay would be short-circuited
        // by the already-applied check above, so it would never be corrected.
        //
        // This way round the crash leaves stock moved and no sale, which converges:
        // the terminal re-uploads, the sale is rebuilt with the SAME terminal-assigned
        // id, the stock port recognises the document and applies nothing, and the sale
        // then saves.
        var stockResult = await stock.PostAsync(
            new StockPostingRequest
            {
                WarehouseId = payload.WarehouseId,
                Kind = StockPostingKind.Sale,
                DocumentId = sale.Id,
                DocumentNumber = sale.ReceiptNumber.ToString(),
                OccurredAt = payload.CompletedAt,
                BusinessDate = payload.BusinessDate,
                UserId = payload.CashierId,

                // The cost snapshotted on the line at the moment of sale, not today's
                // average. A return processed weeks later must reverse at the cost that
                // was actually charged out, and margin reporting has to agree with what
                // the receipt said (ADR 023).
                Lines = [.. sale.Lines.Select(l =>
                    new StockPostingLine(l.VariantId, l.Quantity, l.UnitCostAtSale))]
            },
            cancellationToken);

        if (stockResult.IsFailure)
        {
            // Rejected rather than retried, like any other bad record: nothing has been
            // saved on this side, and blocking the batch would stop the valid sales
            // queued behind it from ever reaching HQ.
            return Result.Failure(stockResult.Error);
        }

        db.Sales.Add(sale);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
        {
            // Another upload of the identical sale won the race. The constraint did its
            // job and the sale IS persisted, so this is success — reporting an error
            // would send the terminal into an endless retry of a sale that exists.
            db.ChangeTracker.Clear();
            return Result.Success();
        }

        await FiscaliseAsync(sale, payload, cancellationToken);
        await RecordPaymentsAsync(sale, payload, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Records the sale's electronic tenders as payments.
    /// </summary>
    /// <remarks>
    /// Same stance as fiscalisation: after the sale is durable, and a failure is logged
    /// rather than rejecting the record. The money was taken at the till, so refusing
    /// the upload would lose the sale to protect a downstream record — the wrong way
    /// round. A tender that failed to record is caught by the Sale ↔ Payment report.
    ///
    /// CASH IS EXCLUDED HERE, at the boundary. It has no electronic-payment lifecycle;
    /// it is drawer money and belongs to the shift's reconciliation, not this module's.
    /// Sending it through the recording port would force a cash "payment" with no
    /// provider and no settlement, which the payments model does not have a place for.
    /// </remarks>
    private async Task RecordPaymentsAsync(Sale sale, SaleSyncPayload payload, CancellationToken cancellationToken)
    {
        var electronic = payload.Tenders
            .Select((tender, index) => (tender, index))
            .Where(t => t.tender.Method != TenderMethod.Cash)
            .Select(t => new RecordedTender(
                t.index,
                t.tender.Method.ToString(),
                new Money(t.tender.Amount, sale.Currency),
                t.tender.TakenAt,
                t.tender.Reference))
            .ToList();

        if (electronic.Count == 0)
            return;

        var result = await payments.RecordSaleTendersAsync(
            new RecordSaleTendersRequest
            {
                BranchId = sale.BranchId,
                TerminalId = sale.TerminalId,
                SaleId = sale.Id,
                BusinessDate = payload.BusinessDate,
                Tenders = electronic
            },
            cancellationToken);

        if (result.IsFailure)
        {
            logger.LogError(
                "Sale {ReceiptNumber} ({SaleId}) was recorded but its tenders could not be recorded as payments: {Code} {Message}",
                sale.ReceiptNumber.ToString(),
                sale.Id,
                result.Error.Code,
                result.Error.Message);
        }
    }

    /// <summary>
    /// Issues the fiscal document for a sale that is now durable.
    /// </summary>
    /// <remarks>
    /// AFTER the sale is saved, and a failure NEVER rejects the record. Both halves are
    /// deliberate and neither is obvious.
    /// <para>
    /// Fiscalising first and rejecting on failure would be the stricter-looking choice
    /// and is the wrong one: the customer has already paid and left, so refusing the
    /// upload would leave money taken with no record of the sale anywhere in the
    /// system. An unfiscalised sale is a compliance problem; an unrecorded sale is a
    /// missing-money problem, and it is the worse of the two.
    /// </para>
    /// <para>
    /// The gap this leaves — a sale saved, its document never issued, because the
    /// process died in between — is not swept under the carpet. It is precisely what
    /// the Sale ↔ Fiscal reconciliation report is for, which is why that report was
    /// worth building before this path existed rather than after.
    /// </para>
    /// </remarks>
    private async Task FiscaliseAsync(Sale sale, SaleSyncPayload payload, CancellationToken cancellationToken)
    {
        var result = await fiscal.FiscaliseSaleAsync(
            new FiscaliseSaleRequest
            {
                CompanyId = sale.CompanyId,
                BranchId = sale.BranchId,
                TerminalId = sale.TerminalId,
                SaleId = sale.Id,
                Currency = sale.Currency,
                IssuedAt = payload.CompletedAt,
                BusinessDate = payload.BusinessDate,
                TotalExclusiveTax = sale.TotalExclusiveTax.Amount,
                TotalTax = sale.TotalTax.Amount,
                TotalInclusiveTax = sale.TotalInclusiveTax.Amount,

                // The terminal was disconnected when it rang this up — that is why the
                // sale is arriving by sync rather than having been fiscalised live.
                IssuedOffline = true,

                Lines = [.. sale.Lines.Select(l => new FiscaliseSaleLine(
                    l.LineNumber,
                    l.Description,
                    l.Quantity,
                    l.UnitOfMeasure,
                    l.UnitPrice.Amount,
                    l.DiscountAmount.Amount,
                    l.TaxCode,
                    l.TaxRate,
                    l.TaxAmount.Amount,
                    l.GrossAmount.Amount))]
            },
            cancellationToken);

        if (result.IsFailure)
        {
            logger.LogError(
                "Sale {ReceiptNumber} ({SaleId}) was recorded but could not be fiscalised: {Code} {Message}",
                sale.ReceiptNumber.ToString(),
                sale.Id,
                result.Error.Code,
                result.Error.Message);
        }
    }

    /// <summary>
    /// Replays the terminal's steps through the aggregate's own state machine.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a property-by-property projection into a Sale. Going through
    /// Open / AddLine / ApplyPricing / AddTender / Complete means every invariant the
    /// domain enforces is enforced on ingest too — an under-tendered sale, a currency
    /// mismatch, an empty basket — and a violation surfaces as a rejected record with a
    /// reason instead of a corrupt row nobody notices until reconciliation.
    ///
    /// <c>terminalIsOnline: true</c> is passed to AddTender, and that is correct rather
    /// than convenient: these tenders were ALREADY ACCEPTED at the till. ADR 038's
    /// offline restriction governs whether a terminal may take a balance-bearing
    /// instrument while disconnected — a decision made at the point of sale, by the
    /// terminal, before this record existed. Re-litigating it here would reject sales
    /// the customer has already walked out with, turning a policy control into data
    /// loss. Enforcing that rule is the terminal's job; recording the outcome is ours.
    /// </remarks>
    private static Result<Sale> Reconstitute(Guid tenantId, SaleSyncPayload payload)
    {
        var currency = payload.Currency;

        var sale = Sale.Open(
            tenantId,
            payload.CompanyId,
            payload.BranchId,
            payload.TerminalId,
            payload.ShiftId,
            payload.CashierId,
            currency,
            new ReceiptNumber(payload.ReceiptSeries, payload.ReceiptSequence),
            payload.BusinessDate,
            payload.OpenedAt,

            // The terminal's own id, not a new one. See SaleSyncPayload.SaleId.
            payload.SaleId);

        foreach (var line in payload.Lines.OrderBy(l => l.LineNumber))
        {
            var added = sale.AddLine(SaleLine.Create(
                line.VariantId,
                line.Description,
                line.Quantity,
                line.UnitOfMeasure,
                new Money(line.UnitPrice, currency),
                line.TaxCode,
                line.TaxRate,
                line.TaxInclusivePricing,
                new Money(line.UnitCostAtSale, currency),
                line.PriceListId,
                line.PriceListVersion));

            if (added.IsFailure)
                return Result<Sale>.Failure(added.Error);
        }

        // The terminal's own line amounts, applied as-is. See SaleSyncPayload for why
        // they are not recomputed here.
        var priced = sale.ApplyPricing(
            [.. payload.Lines
                .OrderBy(l => l.LineNumber)
                .Select((l, index) => new LinePricing(
                    index + 1,
                    new Money(l.Discount, currency),
                    new Money(l.Net, currency),
                    new Money(l.Tax, currency),
                    new Money(l.Gross, currency)))],
            new Money(payload.RoundingAdjustment, currency));

        if (priced.IsFailure)
            return Result<Sale>.Failure(priced.Error);

        foreach (var tender in payload.Tenders)
        {
            var applied = sale.AddTender(
                Tender.Create(
                    tender.Method,
                    new Money(tender.Amount, currency),
                    tender.TakenAt,
                    tender.Reference,
                    tender.PaymentId),
                terminalIsOnline: true);

            if (applied.IsFailure)
                return Result<Sale>.Failure(applied.Error);
        }

        var completed = sale.Complete(payload.CompletedAt);

        return completed.IsFailure
            ? Result<Sale>.Failure(completed.Error)
            : Result<Sale>.Success(sale);
    }
}
