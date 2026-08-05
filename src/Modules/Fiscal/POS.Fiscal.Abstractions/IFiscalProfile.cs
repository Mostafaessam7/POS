using POS.SharedKernel;

namespace POS.Fiscal.Abstractions;

/// <summary>
/// Everything a jurisdiction needs in order to turn a completed sale into a legally
/// valid document, expressed as capabilities the core can interrogate.
/// </summary>
/// <remarks>
/// <para>
/// The core domain does not know that Saudi Arabia requires a cryptographic stamp,
/// that Italy routes B2B invoices through SdI, or that Portugal chains document
/// hashes. It knows only that it holds an <see cref="IFiscalProfile"/> and can ask it
/// questions. Adding a country means adding an assembly, not editing the Sale
/// aggregate. See ADR 031.
/// </para>
/// <para>
/// A profile is resolved per <c>Company</c>, because the taxable person is the legal
/// entity, not the tenant and not the store.
/// </para>
/// </remarks>
public interface IFiscalProfile
{
    /// <summary>Stable identifier, e.g. GENERIC, SA_ZATCA_P2, EG_ETA, IT_SDI, PT_AT.</summary>
    public string Code { get; }

    /// <summary>ISO 3166-1 alpha-2 codes this profile is valid for.</summary>
    public IReadOnlyCollection<string> CountryCodes { get; }

    /// <summary>
    /// Decides which legal document a sale produces — which is not a property of the
    /// sale alone.
    /// </summary>
    /// <remarks>
    /// The same basket is a simplified receipt for a walk-in shopper and a standard
    /// tax invoice for a business customer who supplied a VAT number, and in most
    /// mandate jurisdictions those two follow entirely different legal paths. This is
    /// the first decision in the pipeline because everything downstream depends on it.
    /// </remarks>
    public FiscalDocumentType ResolveDocumentType(FiscalContext context);

    /// <summary>
    /// What this jurisdiction permits for the given document type.
    /// </summary>
    /// <remarks>
    /// Capabilities are per document TYPE, not per country. In Saudi Arabia a
    /// simplified B2C invoice may be issued at the till and reported afterwards,
    /// while a standard B2B invoice must be cleared by ZATCA before it is handed
    /// over. Modelling capability at country granularity would force the stricter
    /// rule onto ordinary retail and destroy offline selling for no legal reason.
    /// </remarks>
    public FiscalCapabilities GetCapabilities(FiscalDocumentType documentType);

    public IFiscalNumberingStrategy Numbering { get; }
    public IFiscalDocumentBuilder Builder { get; }
    public IFiscalSigner? Signer { get; }
    public IFiscalTransmitter? Transmitter { get; }
    public IFiscalQrGenerator? QrGenerator { get; }
    public IFiscalArchiveExporter? ArchiveExporter { get; }

    /// <summary>Pre-issue validation, e.g. "a standard invoice requires a buyer tax number".</summary>
    public Result Validate(FiscalContext context);
}

/// <summary>
/// What a jurisdiction allows for one document type. The core reads this instead of
/// containing any country logic of its own.
/// </summary>
public sealed record FiscalCapabilities
{
    /// <summary>Whether a terminal may issue this document while disconnected.</summary>
    public required OfflineIssuance OfflineIssuance { get; init; }

    /// <summary>How the document reaches the tax authority, if at all.</summary>
    public required TransmissionModel TransmissionModel { get; init; }

    /// <summary>
    /// How long after issuance the document must reach the authority.
    /// Null when there is no deadline.
    /// </summary>
    /// <remarks>
    /// Drives an operational alarm, not just a retry policy. A store whose uplink has
    /// been down for 20 hours under a 24-hour reporting obligation has a compliance
    /// problem that support must be told about BEFORE the deadline passes, not a
    /// queue that quietly keeps retrying.
    /// </remarks>
    public TimeSpan? TransmissionDeadline { get; init; }

    /// <summary>True when the document must carry a cryptographic signature or stamp.</summary>
    public bool RequiresSignature { get; init; }

    /// <summary>
    /// True when each document must incorporate a hash of its predecessor.
    /// </summary>
    /// <remarks>
    /// Chaining (Portugal, and the ZATCA previous-invoice hash) turns numbering into a
    /// strictly ordered, single-writer problem: document N cannot be built until N−1
    /// exists and is final. That constraint reaches all the way back into terminal
    /// design, which is why it is surfaced as a capability rather than buried in a
    /// signer implementation.
    /// </remarks>
    public bool RequiresDocumentChaining { get; init; }

    public bool RequiresQrCode { get; init; }

    /// <summary>True where the legal record is a certified device, not our software.</summary>
    /// <remarks>
    /// Italy's RT devices and Poland's online cash registers mean the fiscal record is
    /// produced by hardware we drive rather than by us. The document we hold is then a
    /// commercial copy, and reconciliation against the device becomes the compliance
    /// task. Very different integration shape from a pure API mandate.
    /// </remarks>
    public bool RequiresCertifiedDevice { get; init; }

    /// <summary>True when a rejected document must be corrected by credit note rather than reissue.</summary>
    public bool CorrectionByCreditNoteOnly { get; init; } = true;

    /// <summary>The profile with no obligations, used by jurisdictions without a mandate.</summary>
    public static FiscalCapabilities None => new()
    {
        OfflineIssuance = OfflineIssuance.Permitted,
        TransmissionModel = TransmissionModel.None
    };
}

/// <summary>
/// Whether a sale can legally complete on a disconnected terminal.
/// </summary>
/// <remarks>
/// THE decision that determines whether this product can be sold in a country at all,
/// and the one genuine collision between the platform's offline-first architecture
/// (ADR 003) and fiscal law. It is modelled explicitly rather than assumed, because
/// the failure mode of assuming wrongly is issuing thousands of invalid invoices.
/// See ADR 032.
/// </remarks>
public enum OfflineIssuance
{
    /// <summary>
    /// The document is legally issued at the terminal; the authority is told later.
    /// Covers most B2C retail, including ZATCA simplified invoices and the generic
    /// no-mandate case. This is the path that keeps offline selling viable.
    /// </summary>
    Permitted = 0,

    /// <summary>
    /// Issuable offline, but clearance must follow within
    /// <see cref="FiscalCapabilities.TransmissionDeadline"/>, and rejection after the
    /// fact must be corrected by credit note — the customer has already left.
    /// </summary>
    PermittedWithDeferredClearance = 1,

    /// <summary>
    /// The authority must approve before the document exists. A disconnected terminal
    /// CANNOT complete this sale. The correct behaviour is to refuse the document type
    /// and offer a lawful alternative — typically a simplified receipt — not to sell
    /// anyway and reconcile later.
    /// </summary>
    Prohibited = 2
}

public enum TransmissionModel
{
    /// <summary>No authority integration.</summary>
    None = 0,

    /// <summary>Issue first, transmit after. Egypt ETA, ZATCA simplified.</summary>
    PostAuditReporting = 1,

    /// <summary>Authority approves before issuance. Italy SdI B2B, ZATCA standard, Mexico CFDI.</summary>
    Clearance = 2,

    /// <summary>Periodic file submission rather than per document. Portugal SAF-T, Poland JPK.</summary>
    PeriodicFiling = 3,

    /// <summary>Recorded by certified hardware; we reconcile against it.</summary>
    CertifiedDevice = 4
}

public enum FiscalDocumentType
{
    SimplifiedInvoice = 0,
    StandardInvoice = 1,
    CreditNote = 2,
    DebitNote = 3,
    /// <summary>Non-fiscal: proforma, quotation, delivery note.</summary>
    NonFiscal = 4
}

/// <summary>
/// Everything a profile needs to make its decisions, assembled by the core.
/// </summary>
/// <remarks>
/// Deliberately a flat, serialisable snapshot rather than a reference to the Sale
/// aggregate. Two reasons: a plugin must not be able to mutate core domain state, and
/// the context must survive being queued for days on a terminal that is offline. It
/// is also the module boundary — Fiscal never references Sales.
/// </remarks>
public sealed record FiscalContext
{
    public required Guid CompanyId { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }
    public required string CountryCode { get; init; }
    public required string SellerTaxRegistration { get; init; }

    public required Guid SaleId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required string Currency { get; init; }

    public required IReadOnlyList<FiscalLine> Lines { get; init; }
    public required decimal TotalExclusiveTax { get; init; }
    public required decimal TotalTax { get; init; }
    public required decimal TotalInclusiveTax { get; init; }

    /// <summary>Null for an anonymous walk-in, which is what makes it a simplified invoice.</summary>
    public FiscalCounterparty? Buyer { get; init; }

    /// <summary>Set when this document corrects an earlier one.</summary>
    public FiscalReference? Corrects { get; init; }

    public bool IsOffline { get; init; }
}

public sealed record FiscalLine(
    int LineNumber,
    string Description,
    string? ItemCode,
    decimal Quantity,
    string UnitOfMeasure,
    decimal UnitPriceExclusiveTax,
    decimal DiscountAmount,
    string TaxCode,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotalInclusiveTax);

public sealed record FiscalCounterparty(
    string Name,
    string? TaxRegistration,
    string? RegistrationScheme,
    string? AddressLine,
    string? City,
    string? CountryCode);

public sealed record FiscalReference(
    Guid DocumentId,
    string DocumentNumber,
    DateOnly IssuedOn,
    string? AuthorityIdentifier);
