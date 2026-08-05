using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A till. A long-lived, physically-located, semi-trusted device — and a
/// PRINCIPAL IN ITS OWN RIGHT, separate from whoever is logged in on it.
/// </summary>
/// <remarks>
/// Generic auth designs model only users. That fails here for three reasons:
///
///  1. A stolen till must be revocable without touching any user account.
///  2. Data sync must be scoped to one branch — the device determines that, not
///     the cashier, who may work at several stores.
///  3. Receipt numbering is per-terminal (see ReceiptSequence below), because a
///     gap-free chain-wide sequence is impossible when terminals sell offline.
///
/// See ADR 014.
/// </remarks>
public sealed class Terminal : AggregateRoot<Guid>, ITenantScoped, IAuditable
{
    private Terminal() { }

    public static Terminal Enrol(Guid branchId, string code, string name, string certificateThumbprint) => new()
    {
        Id = SequentialId.New(),
        BranchId = branchId,
        Code = code,
        Name = name,
        CertificateThumbprint = certificateThumbprint,
        Status = TerminalStatus.Active,
        ReceiptSequence = 0
    };

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }

    /// <summary>Two-digit code appearing in the receipt number. Immutable once trading.</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Client certificate provisioned at installation. The device credential.
    /// Rotating it is how a lost till is locked out.
    /// </summary>
    public string CertificateThumbprint { get; private set; } = null!;

    public TerminalStatus Status { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>
    /// High-water mark of the terminal's local sequence, as received by the server.
    /// The ordering authority for records created offline — a disconnected till's
    /// wall clock may be days wrong, so timestamps must never order events.
    /// </summary>
    public long LastReceivedSequence { get; private set; }

    /// <summary>Local, gap-free receipt counter. Allocated on the terminal, mirrored here.</summary>
    public long ReceiptSequence { get; private set; }

    public void RecordSync(long sequence, DateTimeOffset now)
    {
        if (sequence < LastReceivedSequence)
            throw new InvalidOperationException(
                $"Sequence regression for terminal {Code}: received {sequence}, " +
                $"already have {LastReceivedSequence}. Indicates a restored backup " +
                "or a cloned terminal — requires operator investigation.");

        LastReceivedSequence = sequence;
        LastSyncedAt = now;
    }

    /// <summary>Revokes the device credential. Effective on next server contact.</summary>
    public void Revoke() => Status = TerminalStatus.Revoked;

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
}

public enum TerminalStatus { Active = 0, Suspended = 1, Revoked = 2 }
