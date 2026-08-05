using POS.SharedKernel;

namespace POS.Fiscal.Abstractions;

// ---------------------------------------------------------------------------
// The extension seams.
//
// Six narrow interfaces rather than one IFiscalProvider with sixteen methods.
// Jurisdictions vary along INDEPENDENT axes: Portugal needs chained signing but
// only periodic filing; Egypt needs per-document transmission but no chaining;
// a no-mandate country needs numbering and nothing else. A single fat interface
// would force every plugin to implement — and every reviewer to read — a pile of
// methods that throw NotSupportedException.
//
// Nullable properties on IFiscalProfile express "this jurisdiction has no such
// concept" directly, so absence is a compile-time-visible fact rather than a
// runtime surprise.
// ---------------------------------------------------------------------------

/// <summary>Allocates the legally significant document number.</summary>
/// <remarks>
/// <para>
/// Separate from the platform's internal identifiers on purpose. A fiscal number
/// obeys legal rules — gap-free within a series, sometimes per establishment,
/// sometimes per certified device, often resetting annually — that have nothing to do
/// with UUID v7 machine identity (ADR 005). Conflating the two produces a system that
/// cannot satisfy either requirement.
/// </para>
/// <para>
/// Gap-free numbering allocated on an OFFLINE terminal is only possible because each
/// terminal owns its own series. A single shared series would require coordination
/// and therefore connectivity, which is why the platform never had one.
/// </para>
/// </remarks>
public interface IFiscalNumberingStrategy
{
    public Task<Result<FiscalNumber>> AllocateAsync(
        FiscalContext context,
        FiscalDocumentType documentType,
        CancellationToken ct = default);

    /// <summary>True when the allocated numbers must be consumed in strict order with no gaps.</summary>
    public bool IsGapFree { get; }
}

/// <summary>
/// The allocated number, split into the parts jurisdictions actually care about.
/// </summary>
public sealed record FiscalNumber(
    string Series,
    long Sequence,
    string FormattedNumber,
    int? FiscalYear = null);

/// <summary>Maps the neutral <see cref="FiscalContext"/> into the jurisdiction's payload.</summary>
/// <remarks>
/// Where UBL 2.1 (ZATCA, and most of the EU), FatturaPA XML (Italy), CFDI (Mexico) or
/// a JSON schema (Egypt) is produced. Emits bytes plus a content type, because the
/// core must be able to store, hash, queue and archive a payload it cannot parse.
/// </remarks>
public interface IFiscalDocumentBuilder
{
    public Task<Result<FiscalPayload>> BuildAsync(
        FiscalContext context,
        FiscalNumber number,
        FiscalDocumentType documentType,
        CancellationToken ct = default);
}

/// <summary>An opaque jurisdiction-specific document body.</summary>
/// <param name="ContentType">e.g. application/xml, application/json.</param>
/// <param name="Content">The serialised document.</param>
/// <param name="CanonicalHash">
/// Hash over the canonical form, used for chaining and tamper evidence. Computed by
/// the builder because canonicalisation rules are jurisdiction-specific — the core
/// cannot know which whitespace or attribute ordering is significant.
/// </param>
public sealed record FiscalPayload(
    string ContentType,
    byte[] Content,
    string CanonicalHash);

/// <summary>Applies the cryptographic signature or stamp.</summary>
/// <remarks>
/// <see cref="CanSignOffline"/> is the property that matters architecturally. A signer
/// holding a key provisioned onto the terminal (ZATCA's per-device CSID) can sign a
/// disconnected sale; one calling a remote HSM or a server-held certificate cannot,
/// and the sale must then be refused rather than issued unsigned. The core reads this
/// flag at shift open to decide whether offline trading is permitted at all.
/// </remarks>
public interface IFiscalSigner
{
    public Task<Result<FiscalSignature>> SignAsync(
        FiscalPayload payload,
        FiscalContext context,
        string? previousDocumentHash,
        CancellationToken ct = default);

    public bool CanSignOffline { get; }
}

public sealed record FiscalSignature(
    string Algorithm,
    string Value,
    string? CertificateThumbprint,
    byte[]? SignedContent);

/// <summary>Moves the document to the tax authority.</summary>
/// <remarks>
/// Implementations must be idempotent on <c>documentId</c>. A terminal that loses
/// power mid-submission will retry, and a duplicate submission is a compliance
/// incident in a clearance regime. This is the same reasoning as the sync ingest
/// design in Phase 2 (ADR 018) and for the same reason: at-least-once delivery with
/// an idempotent receiver is achievable, exactly-once is not.
/// </remarks>
public interface IFiscalTransmitter
{
    public Task<Result<FiscalTransmissionResult>> TransmitAsync(
        Guid documentId,
        FiscalPayload payload,
        FiscalSignature? signature,
        FiscalContext context,
        CancellationToken ct = default);

    /// <summary>Re-queries the authority when a response was lost rather than never sent.</summary>
    public Task<Result<FiscalTransmissionResult>> QueryStatusAsync(
        Guid documentId,
        CancellationToken ct = default);
}

public sealed record FiscalTransmissionResult(
    FiscalTransmissionStatus Status,
    string? AuthorityIdentifier,
    string? AuthorityQrPayload,
    IReadOnlyList<FiscalAuthorityMessage> Messages,
    DateTimeOffset RespondedAt);

public enum FiscalTransmissionStatus
{
    Accepted = 0,
    /// <summary>Accepted with non-blocking observations. Still valid; log and move on.</summary>
    AcceptedWithWarnings = 1,
    Rejected = 2,
    /// <summary>Authority received it and has not decided. Poll, do not resubmit.</summary>
    Pending = 3,
    /// <summary>Could not reach the authority. Retry; this is not a rejection.</summary>
    Unreachable = 4
}

/// <remarks>
/// <see cref="IsRetryable"/> separates "our payload is wrong" from "their service is
/// having a bad afternoon". Retrying a schema violation forever is how a queue fills
/// up and a store stops trading; failing permanently on a 503 is how a day's invoices
/// get abandoned.
/// </remarks>
public sealed record FiscalAuthorityMessage(
    string Code,
    string Text,
    FiscalMessageSeverity Severity,
    bool IsRetryable);

public enum FiscalMessageSeverity { Information = 0, Warning = 1, Error = 2 }

/// <summary>Produces the QR payload printed on the receipt.</summary>
/// <remarks>
/// Encodings differ substantially — ZATCA uses base64 TLV, Portugal a delimited
/// string, others a URL — so the core treats the result as an opaque string handed to
/// the printer. Note the ordering constraint: in clearance regimes the QR may depend
/// on the authority's response, so generation runs after transmission, whereas in
/// reporting regimes it runs before. The pipeline honours both.
/// </remarks>
public interface IFiscalQrGenerator
{
    public Result<string> Generate(
        FiscalContext context,
        FiscalNumber number,
        FiscalSignature? signature,
        FiscalTransmissionResult? transmission);
}

/// <summary>Produces periodic statutory files — SAF-T, JPK, audit exports.</summary>
public interface IFiscalArchiveExporter
{
    public Task<Result<FiscalArchive>> ExportAsync(
        Guid companyId,
        DateOnly periodStart,
        DateOnly periodEnd,
        CancellationToken ct = default);
}

public sealed record FiscalArchive(string FileName, string ContentType, byte[] Content);

/// <summary>Resolves the profile for a company.</summary>
/// <remarks>
/// Resolution failure is deliberately an error rather than a silent fallback to
/// GENERIC. Quietly degrading a Saudi company to no-fiscalisation would produce
/// invoices that look fine on the receipt and are not legally valid — discovered at
/// audit, months of trading later.
/// </remarks>
public interface IFiscalProfileRegistry
{
    public Result<IFiscalProfile> Resolve(string fiscalProfileCode);
    public IReadOnlyCollection<IFiscalProfile> All { get; }
}
