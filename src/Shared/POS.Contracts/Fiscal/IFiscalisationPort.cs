using POS.SharedKernel;

namespace POS.Contracts.Fiscal;

/// <summary>
/// The seam through which a completed sale becomes a fiscal document.
/// </summary>
/// <remarks>
/// <para>
/// Sales must not reference the Fiscal module, and emphatically must not learn what a
/// fiscal profile is: the entire point of ADR 031 is that no jurisdiction knowledge
/// leaks into the core. This request therefore describes a SALE, not a document —
/// no profile code, no country, no document type. Deciding what kind of document a
/// jurisdiction requires for this sale is the profile's job, behind the port.
/// </para>
/// <para>
/// The seller's fiscal identity is likewise absent. Country and tax registration belong
/// to the company, which lives in Identity; the adapter resolves them through
/// <see cref="Identity.ICompanyDirectory"/> rather than making every caller carry
/// fields it has no business knowing.
/// </para>
/// </remarks>
public interface IFiscalisationPort
{
    /// <summary>
    /// Issues the fiscal document a completed sale requires, once.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT BY SALE. A second call for the same sale returns the document already
    /// issued rather than a new one. Fiscal numbering is gap-free and sequential
    /// (ADR 005), so a duplicate is not merely untidy — it burns a number, and in most
    /// regimes both a gap and a duplicate in a fiscal series are findings an auditor
    /// raises.
    /// </remarks>
    public Task<Result<FiscalisationOutcome>> FiscaliseSaleAsync(
        FiscaliseSaleRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A completed sale, in the terms fiscalisation needs.</summary>
public sealed record FiscaliseSaleRequest
{
    public required Guid CompanyId { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }

    /// <summary>The sale being fiscalised. Also the idempotency key.</summary>
    public required Guid SaleId { get; init; }

    public required string Currency { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateOnly BusinessDate { get; init; }

    public required decimal TotalExclusiveTax { get; init; }
    public required decimal TotalTax { get; init; }
    public required decimal TotalInclusiveTax { get; init; }

    public required IReadOnlyList<FiscaliseSaleLine> Lines { get; init; }

    /// <summary>
    /// Whether the terminal was disconnected when the sale was rung up.
    /// </summary>
    /// <remarks>
    /// Not a detail. Some regimes prohibit issuing certain documents offline entirely,
    /// and the pipeline checks that BEFORE allocating a number so a refused document
    /// does not burn one out of a gap-free series.
    /// </remarks>
    public bool IssuedOffline { get; init; }
}

public sealed record FiscaliseSaleLine(
    int LineNumber,
    string Description,
    decimal Quantity,
    string UnitOfMeasure,
    decimal UnitPriceExclusiveTax,
    decimal DiscountAmount,
    string TaxCode,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotalInclusiveTax);

/// <summary>What the caller needs back: enough to print a receipt, and nothing more.</summary>
public sealed record FiscalisationOutcome(
    Guid DocumentId,
    string FormattedNumber,
    string? QrPayload);
