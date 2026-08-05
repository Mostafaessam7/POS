namespace POS.Sync.Contracts;

/// <summary>
/// Wire contracts for terminal synchronisation.
/// </summary>
/// <remarks>
/// VERSIONED FROM MESSAGE ONE. Terminals in the field run whatever build was
/// installed when the store opened; a chain will routinely have three versions
/// live. A protocol without a version field cannot be evolved without a flag-day
/// upgrade across every till simultaneously, which is not something a retailer will
/// agree to.
/// </remarks>
public static class SyncProtocol
{
    public const string CurrentVersion = "1.0";

    /// <summary>Oldest version the server still accepts. Terminals below this must update.</summary>
    public const string MinimumSupportedVersion = "1.0";
}

/// <summary>Upward: transactional records from a terminal.</summary>
public sealed record UploadBatchRequest(
    string ProtocolVersion,
    Guid TerminalId,
    long FirstSequence,
    long LastSequence,
    IReadOnlyList<SyncRecord> Records);

/// <summary>
/// One record in a batch. Deliberately opaque — the sync module routes by
/// <see cref="Type"/> and never deserialises the payload itself.
/// </summary>
/// <remarks>
/// Keeping sync ignorant of domain shapes is what allows Sales and Inventory to
/// evolve their contracts without touching the transport, and prevents Sync from
/// accumulating a project reference to every module (architecture rule 2).
/// </remarks>
public sealed record SyncRecord(
    Guid RecordId,
    long TerminalSequence,
    string Type,
    string Payload,
    DateTimeOffset OccurredAtLocal);

public sealed record UploadBatchResponse(
    Guid BatchId,
    int Accepted,
    int Duplicates,
    IReadOnlyList<RejectedRecord> Rejected);

/// <summary>
/// A record the server refused.
/// </summary>
/// <remarks>
/// Rejections are reported per record rather than failing the batch. One
/// malformed record must not block the twelve valid sales behind it — the terminal
/// would retry forever and the store's takings would never reach HQ.
/// </remarks>
public sealed record RejectedRecord(Guid RecordId, string Code, string Reason);

/// <summary>Downward: master-data delta request.</summary>
public sealed record PullMasterDataRequest(
    string ProtocolVersion,
    Guid TerminalId,
    IReadOnlyDictionary<string, long> Cursors);

public sealed record PullMasterDataResponse(
    IReadOnlyDictionary<string, long> Versions,
    IReadOnlyList<MasterDataChange> Changes,
    bool IsFullSnapshot,
    bool HasMore);

public sealed record MasterDataChange(
    string EntityType,
    Guid EntityId,
    long Version,
    ChangeOperation Operation,
    string? Payload);

public enum ChangeOperation
{
    Upsert = 0,

    /// <summary>
    /// Soft removal. Terminals mark the record unsellable but RETAIN it — a return
    /// against a discontinued product must still resolve its description.
    /// </summary>
    Remove = 1
}
