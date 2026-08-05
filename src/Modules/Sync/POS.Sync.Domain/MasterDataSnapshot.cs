using POS.SharedKernel;

namespace POS.Sync.Domain;

/// <summary>
/// A versioned, immutable master-data snapshot published downward to terminals.
/// </summary>
/// <remarks>
/// Terminals pull with a cursor: "give me everything after version N". The server
/// responds with a delta, or with a full snapshot when the terminal has fallen too
/// far behind for deltas to be retained.
///
/// The FULL-SNAPSHOT FALLBACK is not an edge case. A till returned from repair
/// after six weeks, or a new store opening, both need it — and a delta chain that
/// only works for recently-connected terminals is a support burden that surfaces
/// at the worst possible time.
/// </remarks>
public sealed class MasterDataVersion : Entity<Guid>, ITenantScoped
{
    private MasterDataVersion() { }

    public static MasterDataVersion Create(string entityType, long version, DateTimeOffset now) => new()
    {
        Id = SequentialId.New(),
        EntityType = entityType,
        Version = version,
        PublishedAt = now
    };

    public Guid TenantId { get; private set; }

    /// <summary>"Product", "PriceList", "TaxGroup", "PermissionBundle".</summary>
    public string EntityType { get; private set; } = null!;

    /// <summary>
    /// Monotonic per tenant and entity type. Terminals store the last version they
    /// hold and request everything above it.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }
}

/// <summary>Where a terminal has reached in each master-data stream.</summary>
public sealed class TerminalSyncCursor : Entity<Guid>, ITenantScoped
{
    private TerminalSyncCursor() { }

    public static TerminalSyncCursor Create(Guid terminalId, string entityType) => new()
    {
        Id = SequentialId.New(),
        TerminalId = terminalId,
        EntityType = entityType,
        AcknowledgedVersion = 0
    };

    public Guid TenantId { get; private set; }
    public Guid TerminalId { get; private set; }
    public string EntityType { get; private set; } = null!;

    /// <summary>
    /// Advanced only on the terminal's EXPLICIT acknowledgement, never on send.
    /// A cursor advanced on send loses data whenever a response is dropped.
    /// </summary>
    public long AcknowledgedVersion { get; private set; }

    public DateTimeOffset? LastAcknowledgedAt { get; private set; }

    public void Acknowledge(long version, DateTimeOffset now)
    {
        if (version < AcknowledgedVersion) return;   // stale ack; ignore

        AcknowledgedVersion = version;
        LastAcknowledgedAt = now;
    }
}
