using POS.SharedKernel;

namespace POS.Fiscal.Domain;

/// <summary>
/// The legal envelope around a commercial sale.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a SEPARATE aggregate from <c>Sale</c>, and this is the decision that makes
/// the whole design work (ADR 033). A sale is a commercial fact: goods left, money
/// arrived. A fiscal document is a legal artefact about that fact, and the two have
/// genuinely different lifecycles — a sale is complete the moment the customer walks
/// out, while its document may still be queued for clearance the next morning, may be
/// rejected, and may need superseding by a credit note.
/// </para>
/// <para>
/// Folding fiscal state into Sale would mean every jurisdiction's requirements
/// accreting onto the single most important aggregate in the system, and a sale that
/// cannot be considered complete until a government web service replies. Instead
/// <c>Sale</c> holds a loose reference outward — the same pattern as
/// <c>StockDocumentReference</c> in Phase 4 — and the core stays jurisdiction-free.
/// </para>
/// <para>
/// Immutable once issued, per ADR 007. Corrections are new documents.
/// </para>
/// </remarks>
public sealed class FiscalDocument : AggregateRoot<Guid>, ITenantScoped
{
    private readonly List<FiscalTransmissionAttempt> _attempts = [];

    private FiscalDocument() { }

    public static FiscalDocument Issue(
        Guid tenantId,
        Guid companyId,
        Guid branchId,
        Guid terminalId,
        Guid saleId,
        string profileCode,
        int documentType,
        string series,
        long sequence,
        string formattedNumber,
        string contentType,
        byte[] content,
        string canonicalHash,
        string? previousDocumentHash,
        DateTimeOffset issuedAt,
        DateOnly businessDate,
        bool issuedOffline) => new()
    {
        Id = SequentialId.New(),
        TenantId = tenantId,
        CompanyId = companyId,
        BranchId = branchId,
        TerminalId = terminalId,
        SaleId = saleId,
        ProfileCode = profileCode,
        DocumentType = documentType,
        Series = series,
        Sequence = sequence,
        FormattedNumber = formattedNumber,
        ContentType = contentType,
        Content = content,
        CanonicalHash = canonicalHash,
        PreviousDocumentHash = previousDocumentHash,
        IssuedAt = issuedAt,
        BusinessDate = businessDate,
        IssuedOffline = issuedOffline,
        Status = FiscalDocumentStatus.Issued
    };

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TerminalId { get; private set; }

    /// <summary>Loose reference to the sale. No foreign key — Fiscal never references Sales.</summary>
    public Guid SaleId { get; private set; }

    public string ProfileCode { get; private set; } = null!;
    public int DocumentType { get; private set; }

    public string Series { get; private set; } = null!;
    public long Sequence { get; private set; }
    public string FormattedNumber { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;
    public byte[] Content { get; private set; } = null!;
    public string CanonicalHash { get; private set; } = null!;

    /// <summary>Set in chaining jurisdictions, forming a tamper-evident sequence.</summary>
    public string? PreviousDocumentHash { get; private set; }

    public string? SignatureAlgorithm { get; private set; }
    public string? SignatureValue { get; private set; }
    public string? CertificateThumbprint { get; private set; }

    public string? AuthorityIdentifier { get; private set; }
    public string? QrPayload { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }
    public DateOnly BusinessDate { get; private set; }

    /// <summary>
    /// True when issued on a disconnected terminal.
    /// </summary>
    /// <remarks>
    /// Worth persisting rather than inferring. It is the first question asked in any
    /// investigation of a late or rejected submission, and it distinguishes "we were
    /// offline, as the law permits" from "we were online and failed to transmit",
    /// which are different compliance positions entirely.
    /// </remarks>
    public bool IssuedOffline { get; private set; }

    public FiscalDocumentStatus Status { get; private set; }
    public DateTimeOffset? TransmittedAt { get; private set; }

    /// <summary>Deadline by which the authority must have received this. Null when none applies.</summary>
    public DateTimeOffset? TransmissionDueBy { get; private set; }

    public IReadOnlyList<FiscalTransmissionAttempt> Attempts => _attempts.AsReadOnly();

    /// <summary>Set when a later document corrects this one.</summary>
    public Guid? SupersededByDocumentId { get; private set; }

    public void ApplySignature(string algorithm, string value, string? thumbprint)
    {
        EnsureNotFinal();
        SignatureAlgorithm = algorithm;
        SignatureValue = value;
        CertificateThumbprint = thumbprint;
    }

    public void SetQrPayload(string payload) => QrPayload = payload;

    public void SetTransmissionDeadline(DateTimeOffset dueBy) => TransmissionDueBy = dueBy;

    public void RecordAttempt(FiscalTransmissionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        _attempts.Add(attempt);
    }

    public void MarkAccepted(string? authorityIdentifier, DateTimeOffset at)
    {
        Status = FiscalDocumentStatus.Accepted;
        AuthorityIdentifier = authorityIdentifier;
        TransmittedAt = at;
    }

    public void MarkPending(DateTimeOffset at)
    {
        Status = FiscalDocumentStatus.PendingAuthority;
        TransmittedAt = at;
    }

    /// <remarks>
    /// Rejection does NOT delete or rewrite the document. In a reporting jurisdiction
    /// the customer already holds a printed receipt, so the record of what was handed
    /// over must survive exactly as issued; the correction is a separate credit note
    /// carrying a reference back here. Quietly reissuing under the same number would
    /// be the one thing guaranteed to fail an audit.
    /// </remarks>
    public void MarkRejected() => Status = FiscalDocumentStatus.Rejected;

    public void MarkSuperseded(Guid correctingDocumentId)
    {
        SupersededByDocumentId = correctingDocumentId;
        Status = FiscalDocumentStatus.Superseded;
    }

    /// <summary>True when the transmission deadline has passed without acceptance.</summary>
    public bool IsOverdue(DateTimeOffset now) =>
        TransmissionDueBy is { } due
        && now > due
        && Status is FiscalDocumentStatus.Issued or FiscalDocumentStatus.PendingAuthority;

    private void EnsureNotFinal()
    {
        if (Status is FiscalDocumentStatus.Accepted or FiscalDocumentStatus.Superseded)
        {
            throw new InvalidOperationException(
                $"Fiscal document {FormattedNumber} is final and cannot be altered.");
        }
    }
}

public enum FiscalDocumentStatus
{
    /// <summary>Legally issued. In a no-mandate jurisdiction this is terminal.</summary>
    Issued = 0,
    /// <summary>Sent; authority has not yet decided. Poll, do not resubmit.</summary>
    PendingAuthority = 1,
    Accepted = 2,
    Rejected = 3,
    Superseded = 4
}

public sealed record FiscalTransmissionAttempt(
    Guid Id,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    string Outcome,
    string? AuthorityIdentifier,
    string? MessageCode,
    string? MessageText,
    bool IsRetryable);
