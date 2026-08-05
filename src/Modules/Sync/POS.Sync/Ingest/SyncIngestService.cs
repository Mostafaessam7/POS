using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POS.Common.Persistence;
using POS.SharedKernel;
using POS.Sync.Contracts;
using POS.Sync.Domain;
using POS.Sync.Persistence;

namespace POS.Sync.Ingest;

/// <summary>Accepts an upload batch idempotently.</summary>
public sealed class SyncIngestService(
    SyncDbContext db,
    IClock clock,
    IEnumerable<ISyncRecordHandler> handlers,
    ILogger<SyncIngestService> logger)
{
    private readonly Dictionary<string, ISyncRecordHandler> _handlers =
        handlers.ToDictionary(h => h.RecordType, StringComparer.Ordinal);

    public async Task<Result<UploadBatchResponse>> IngestAsync(
        Guid tenantId,
        UploadBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.CompareOrdinal(request.ProtocolVersion, SyncProtocol.MinimumSupportedVersion) < 0)
            return Error.Validation(
                "sync.protocol.unsupported",
                $"Protocol {request.ProtocolVersion} is no longer supported. Update the terminal.");

        var now = clock.UtcNow;
        var batch = SyncBatch.Receive(
            tenantId, request.TerminalId, request.FirstSequence, request.LastSequence,
            request.Records.Count, request.ProtocolVersion, now);

        db.Batches.Add(batch);

        // One round trip rather than one per record. A batch is typically 50-500
        // records; per-record existence checks would dominate the request.
        var sequences = request.Records.Select(r => r.TerminalSequence).ToList();

        var alreadySeen = await db.SyncedRecords
            .Where(r => r.TerminalId == request.TerminalId && sequences.Contains(r.TerminalSequence))
            .Select(r => r.TerminalSequence)
            .ToHashSetAsync(cancellationToken);

        // The terminal's high-water mark. A sequence BELOW it that we have not already
        // seen cannot be a late arrival — sequences are assigned monotonically by the
        // terminal — so it means the counter went backwards. In practice that is a
        // restored backup or a cloned terminal image, and accepting it silently gives
        // two tills minting the same receipt numbers: duplicate receipts, a fiscal
        // series with collisions, and takings that cannot be reconciled to a till.
        //
        // Rejected per record rather than failing the batch, like every other rejection
        // here, so a genuine regression is reported and quarantined while nothing else
        // is blocked behind it.
        var highWaterMark = await db.SyncedRecords
            .Where(r => r.TerminalId == request.TerminalId)
            .Select(r => (long?)r.TerminalSequence)
            .MaxAsync(cancellationToken) ?? 0L;

        var accepted = 0;
        var duplicates = 0;
        var rejected = new List<RejectedRecord>();

        foreach (var record in request.Records.OrderBy(r => r.TerminalSequence))
        {
            // Fast path. The database unique constraint remains the real guarantee —
            // this check only avoids the cost of doing the work twice.
            if (alreadySeen.Contains(record.TerminalSequence))
            {
                duplicates++;
                continue;
            }

            if (record.TerminalSequence < highWaterMark)
            {
                rejected.Add(new RejectedRecord(
                    record.RecordId,
                    "sync.sequence.regression",
                    $"Sequence {record.TerminalSequence} is below this terminal's high-water mark " +
                    $"of {highWaterMark}. The terminal has been restored or cloned."));

                logger.LogWarning(
                    "Sequence regression from terminal {TerminalId}. Sequence={Sequence} HighWaterMark={HighWaterMark}",
                    request.TerminalId,
                    record.TerminalSequence,
                    highWaterMark);

                continue;
            }

            if (!_handlers.TryGetValue(record.Type, out var handler))
            {
                rejected.Add(new RejectedRecord(record.RecordId, "sync.type.unknown",
                    $"No handler registered for '{record.Type}'."));
                continue;
            }

            var result = await handler.HandleAsync(tenantId, record, cancellationToken);

            if (result.IsFailure)
            {
                // Rejected, not retried. A malformed record must never block the
                // valid sales queued behind it — the terminal would retry forever
                // and the store's takings would never reach HQ.
                rejected.Add(new RejectedRecord(record.RecordId, result.Error!.Code, result.Error.Message));

                logger.LogWarning(
                    "Sync record rejected. Terminal={TerminalId} Sequence={Sequence} Type={Type} Code={Code}",
                    request.TerminalId, record.TerminalSequence, record.Type, result.Error.Code);

                continue;
            }

            db.SyncedRecords.Add(SyncedRecord.Create(
                request.TerminalId, record.TerminalSequence, record.RecordId, record.Type, batch.Id, now));

            accepted++;
        }

        batch.MarkProcessed(now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
        {
            // Two uploads of the same batch raced. The constraint did its job;
            // report success, because the records ARE persisted. Returning an
            // error here would cause the terminal to retry forever.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Concurrent duplicate batch from terminal {TerminalId}; treated as already applied.",
                    request.TerminalId);
            }

            return new UploadBatchResponse(batch.Id, 0, request.Records.Count, []);
        }

        return new UploadBatchResponse(batch.Id, accepted, duplicates, rejected);
    }

}

/// <summary>
/// Handles one record type. Implemented inside the OWNING module, registered here.
/// </summary>
/// <remarks>
/// This inversion is what keeps Sync from referencing Sales, Inventory, and every
/// other module — which architecture rule 2 forbids. Sync owns transport; modules
/// own meaning.
/// </remarks>
public interface ISyncRecordHandler
{
    public string RecordType { get; }

    public Task<Result> HandleAsync(Guid tenantId, SyncRecord record, CancellationToken cancellationToken);
}
