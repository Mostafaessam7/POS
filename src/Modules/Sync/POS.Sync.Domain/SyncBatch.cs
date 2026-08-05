using POS.SharedKernel;

namespace POS.Sync.Domain;

/// <summary>
/// A unit of upward sync: transactional records produced by one terminal.
/// </summary>
/// <remarks>
/// THE ARCHITECTURAL PRINCIPLE THAT MAKES OFFLINE SYNC TRACTABLE (ADR 004):
///
///   Master data flows DOWN only.  Transactional data flows UP only.
///
/// HQ owns products, prices, promotions, tax rules and users; terminals receive
/// them as versioned immutable snapshots and never modify them. Terminals own
/// sales, payments, stock movements and shifts; these are append-only facts that
/// flow up, and HQ never edits them.
///
/// Because neither side mutates the same row, there are effectively NO MERGE
/// CONFLICTS. A store-level price override is not an edit to the central record —
/// it is a new master-data record published downward.
///
/// Reject any proposal for bidirectional sync of a mutable Products table with
/// last-write-wins. That is the design that produces the classic POS bug where a
/// store's price silently reverts overnight.
/// </remarks>
public sealed class SyncBatch : AggregateRoot<Guid>, ITenantScoped
{
    private SyncBatch() { }

    public static SyncBatch Receive(
        Guid tenantId,
        Guid terminalId,
        long firstSequence,
        long lastSequence,
        int recordCount,
        string protocolVersion,
        DateTimeOffset now) => new()
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            TerminalId = terminalId,
            FirstSequence = firstSequence,
            LastSequence = lastSequence,
            RecordCount = recordCount,
            ProtocolVersion = protocolVersion,
            ReceivedAt = now,
            Status = SyncBatchStatus.Received
        };

    public Guid TenantId { get; private set; }
    public Guid TerminalId { get; private set; }

    /// <summary>
    /// Terminal-local monotonic counter. THE ORDERING AUTHORITY.
    /// </summary>
    /// <remarks>
    /// Never order by terminal timestamp. An offline till's clock can be minutes or
    /// days wrong — a flat battery resets it to epoch — and sorting by it silently
    /// interleaves transactions from different days.
    /// </remarks>
    public long FirstSequence { get; private set; }

    public long LastSequence { get; private set; }
    public int RecordCount { get; private set; }
    public string ProtocolVersion { get; private set; } = null!;

    /// <summary>Server clock. The only trustworthy timestamp in the pipeline.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }
    public SyncBatchStatus Status { get; private set; }
    public string? FailureReason { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = SyncBatchStatus.Processed;
        ProcessedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        Status = SyncBatchStatus.Failed;
        FailureReason = reason;
        ProcessedAt = now;
    }
}

public enum SyncBatchStatus { Received = 0, Processed = 1, Failed = 2 }

/// <summary>
/// The idempotency ledger. One row per record accepted from a terminal.
/// </summary>
/// <remarks>
/// A unique constraint on (TerminalId, TerminalSequence) is what makes ingest
/// idempotent — enforced BY THE DATABASE, not by an application-level
/// "select then insert", which races under concurrency and silently duplicates
/// under retry.
///
/// This matters because the failure it prevents is a real, weekly occurrence: a
/// terminal uploads a batch, the server commits, the response is lost to a dropped
/// connection, and the terminal retries the identical batch. Without this
/// constraint, that is a duplicated sale.
/// </remarks>
public sealed class SyncedRecord
{
    private SyncedRecord() { }

    public static SyncedRecord Create(
        Guid terminalId,
        long terminalSequence,
        Guid recordId,
        string recordType,
        Guid batchId,
        DateTimeOffset now) => new()
        {
            Id = SequentialId.New(),
            TerminalId = terminalId,
            TerminalSequence = terminalSequence,
            RecordId = recordId,
            RecordType = recordType,
            BatchId = batchId,
            ReceivedAt = now
        };

    public Guid Id { get; private set; }
    public Guid TerminalId { get; private set; }
    public long TerminalSequence { get; private set; }

    /// <summary>The UUID v7 minted on the terminal. Stable across retries.</summary>
    public Guid RecordId { get; private set; }

    public string RecordType { get; private set; } = null!;
    public Guid BatchId { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
}
