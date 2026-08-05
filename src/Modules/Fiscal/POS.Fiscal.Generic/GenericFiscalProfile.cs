using System.Security.Cryptography;
using System.Text.Json;
using POS.Fiscal.Abstractions;
using POS.SharedKernel;

namespace POS.Fiscal.Generic;

/// <summary>
/// The profile for jurisdictions with no mandatory fiscalisation.
/// </summary>
/// <remarks>
/// <para>
/// Not a stub or a null object. It is a real, complete profile that numbers documents
/// per terminal, produces a neutral JSON payload, and hashes it. Two reasons that
/// matters: it is what most of the world actually needs, and it forces the
/// abstraction to be exercised end to end from day one. An extension model whose only
/// implementation does nothing is an extension model nobody has tested.
/// </para>
/// <para>
/// It also sets the honest baseline for what a country plugin costs: implement
/// numbering and a builder, and you have a working profile. Everything else — signing,
/// transmission, QR, archives — is opt-in.
/// </para>
/// </remarks>
public sealed class GenericFiscalProfile(IFiscalNumberingStrategy numbering) : IFiscalProfile
{
    public const string ProfileCode = "GENERIC";

    public string Code => ProfileCode;

    /// <summary>Empty means "any country" — this is the fallback, not a country claim.</summary>
    public IReadOnlyCollection<string> CountryCodes => [];

    public IFiscalNumberingStrategy Numbering { get; } = numbering;
    public IFiscalDocumentBuilder Builder { get; } = new NeutralJsonDocumentBuilder();

    public IFiscalSigner? Signer => null;
    public IFiscalTransmitter? Transmitter => null;
    public IFiscalQrGenerator? QrGenerator => null;
    public IFiscalArchiveExporter? ArchiveExporter => null;

    /// <remarks>
    /// Even with no mandate, the distinction between a walk-in receipt and an invoice
    /// naming a business buyer is a commercial one worth preserving. Keeping it here
    /// means a company later migrating onto a real mandate does not have to
    /// reclassify its history.
    /// </remarks>
    public FiscalDocumentType ResolveDocumentType(FiscalContext context) =>
        context.Corrects is not null
            ? FiscalDocumentType.CreditNote
            : context.Buyer?.TaxRegistration is { Length: > 0 }
                ? FiscalDocumentType.StandardInvoice
                : FiscalDocumentType.SimplifiedInvoice;

    public FiscalCapabilities GetCapabilities(FiscalDocumentType documentType) =>
        FiscalCapabilities.None;

    public Result Validate(FiscalContext context) => Result.Success();
}

/// <summary>Per-terminal gap-free numbering, the platform default.</summary>
/// <remarks>
/// Per terminal rather than per company, because a shared series needs coordination
/// and therefore connectivity — which would silently remove offline selling. Each
/// terminal owning its own series is what makes gap-free numbering achievable on a
/// disconnected till (ADR 005, ADR 014).
/// </remarks>
public sealed class TerminalSeriesNumberingStrategy(IFiscalSequenceAllocator allocator)
    : IFiscalNumberingStrategy
{
    public bool IsGapFree => true;

    public async Task<Result<FiscalNumber>> AllocateAsync(
        FiscalContext context,
        FiscalDocumentType documentType,
        CancellationToken ct = default)
    {
        var series = $"{context.BranchId:N}"[..6].ToUpperInvariant()
                     + "-" + $"{context.TerminalId:N}"[..4].ToUpperInvariant()
                     + "-" + Prefix(documentType);

        var sequenceResult = await allocator.NextAsync(
            context.CompanyId, context.TerminalId, series, ct);

        if (sequenceResult.IsFailure)
        {
            return Result<FiscalNumber>.Failure(sequenceResult.Error);
        }

        var sequence = sequenceResult.Value;

        return Result<FiscalNumber>.Success(new FiscalNumber(
            series,
            sequence,
            $"{series}/{sequence:D8}",
            context.BusinessDate.Year));
    }

    private static string Prefix(FiscalDocumentType type) => type switch
    {
        FiscalDocumentType.SimplifiedInvoice => "S",
        FiscalDocumentType.StandardInvoice => "I",
        FiscalDocumentType.CreditNote => "C",
        FiscalDocumentType.DebitNote => "D",
        _ => "N"
    };
}

/// <summary>
/// Hands out the next number in a series.
/// </summary>
/// <remarks>
/// Separate from the strategy because allocation must be durable and monotonic on the
/// terminal's own store, and it is the one operation that must survive power loss
/// mid-sale without reusing or skipping a number. Implemented against SQLite on the
/// terminal and SQL Server centrally.
/// </remarks>
public interface IFiscalSequenceAllocator
{
    public Task<Result<long>> NextAsync(Guid companyId, Guid terminalId, string series, CancellationToken ct = default);
}

/// <summary>Neutral JSON representation, used where no statutory format is prescribed.</summary>
public sealed class NeutralJsonDocumentBuilder : IFiscalDocumentBuilder
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public Task<Result<FiscalPayload>> BuildAsync(
        FiscalContext context,
        FiscalNumber number,
        FiscalDocumentType documentType,
        CancellationToken ct = default)
    {
        var body = new
        {
            documentType = documentType.ToString(),
            number = number.FormattedNumber,
            number.Series,
            number.Sequence,
            issuedAt = context.IssuedAt,
            businessDate = context.BusinessDate,
            context.Currency,
            seller = new { context.SellerTaxRegistration, context.CountryCode },
            buyer = context.Buyer,
            lines = context.Lines,
            totals = new
            {
                context.TotalExclusiveTax,
                context.TotalTax,
                context.TotalInclusiveTax
            },
            corrects = context.Corrects
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(body, Options);
        var hash = Convert.ToHexString(SHA256.HashData(json));

        return Task.FromResult(Result<FiscalPayload>.Success(
            new FiscalPayload("application/json", json, hash)));
    }
}

/// <summary>Registry over the profiles registered in DI.</summary>
/// <remarks>
/// Plugins are ordinary assemblies registering an <see cref="IFiscalProfile"/> at
/// startup. Deliberately not runtime assembly scanning from a plugins folder: loading
/// unsigned code that produces legal documents is a supply-chain risk far larger than
/// the deployment convenience it buys, and every jurisdiction plugin ships on our own
/// release cadence anyway.
/// </remarks>
public sealed class FiscalProfileRegistry(IEnumerable<IFiscalProfile> profiles) : IFiscalProfileRegistry
{
    private readonly Dictionary<string, IFiscalProfile> _byCode =
        profiles.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IFiscalProfile> All => _byCode.Values;

    public Result<IFiscalProfile> Resolve(string fiscalProfileCode) =>
        _byCode.TryGetValue(fiscalProfileCode, out var profile)
            ? Result<IFiscalProfile>.Success(profile)
            : Result<IFiscalProfile>.Failure(FiscalErrors.ProfileNotFound(fiscalProfileCode));
}
