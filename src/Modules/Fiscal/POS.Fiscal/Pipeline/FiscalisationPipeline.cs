using POS.Fiscal.Abstractions;
using POS.Fiscal.Domain;
using POS.SharedKernel;

namespace POS.Fiscal.Pipeline;

/// <summary>
/// Turns a completed sale into a fiscal document by driving the profile's seams in
/// the correct order.
/// </summary>
/// <remarks>
/// <para>
/// This class is the ONLY place that knows the ordering rules, and it is
/// jurisdiction-neutral: validate, resolve type, check offline legality, number,
/// build, sign, transmit, QR. Countries differ by which steps exist and whether
/// transmission blocks issuance, both of which are read from
/// <see cref="FiscalCapabilities"/> rather than branched on a country code.
/// </para>
/// <para>
/// There is deliberately no <c>if (country == "SA")</c> anywhere in this assembly.
/// If one ever appears, the abstraction has failed and the fix is a new capability
/// flag, not a special case.
/// </para>
/// </remarks>
public sealed class FiscalisationPipeline(
    IFiscalProfileRegistry registry,
    IFiscalDocumentStore store,
    IClock clock)
{
    public async Task<Result<FiscalDocument>> FiscaliseAsync(
        FiscalContext context,
        string fiscalProfileCode,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var profileResult = registry.Resolve(fiscalProfileCode);
        if (profileResult.IsFailure)
        {
            return Result<FiscalDocument>.Failure(profileResult.Error);
        }

        var profile = profileResult.Value;

        var validation = profile.Validate(context);
        if (validation.IsFailure)
        {
            return Result<FiscalDocument>.Failure(validation.Error);
        }

        var documentType = profile.ResolveDocumentType(context);
        var capabilities = profile.GetCapabilities(documentType);

        // The offline gate. Checked BEFORE any number is allocated, because a
        // gap-free series must not burn a number on a document we are about to
        // refuse — a gap is itself a compliance finding in most regimes.
        if (context.IsOffline && capabilities.OfflineIssuance == OfflineIssuance.Prohibited)
        {
            return Result<FiscalDocument>.Failure(FiscalErrors.OfflineIssuanceProhibited(documentType));
        }

        if (context.IsOffline
            && capabilities.RequiresSignature
            && profile.Signer is { CanSignOffline: false })
        {
            return Result<FiscalDocument>.Failure(FiscalErrors.OfflineSigningUnavailable);
        }

        var numberResult = await profile.Numbering.AllocateAsync(context, documentType, ct);
        if (numberResult.IsFailure)
        {
            return Result<FiscalDocument>.Failure(numberResult.Error);
        }

        var number = numberResult.Value;

        string? previousHash = null;
        if (capabilities.RequiresDocumentChaining)
        {
            previousHash = await store.GetLastCanonicalHashAsync(
                context.CompanyId, context.TerminalId, number.Series, ct);
        }

        var payloadResult = await profile.Builder.BuildAsync(context, number, documentType, ct);
        if (payloadResult.IsFailure)
        {
            return Result<FiscalDocument>.Failure(payloadResult.Error);
        }

        var payload = payloadResult.Value;

        var document = FiscalDocument.Issue(
            tenantId, context.CompanyId, context.BranchId, context.TerminalId,
            context.SaleId, profile.Code, (int)documentType,
            number.Series, number.Sequence, number.FormattedNumber,
            payload.ContentType, payload.Content, payload.CanonicalHash,
            previousHash, context.IssuedAt, context.BusinessDate, context.IsOffline);

        FiscalSignature? signature = null;
        if (capabilities.RequiresSignature && profile.Signer is { } signer)
        {
            var signatureResult = await signer.SignAsync(payload, context, previousHash, ct);
            if (signatureResult.IsFailure)
            {
                return Result<FiscalDocument>.Failure(signatureResult.Error);
            }

            signature = signatureResult.Value;
            document.ApplySignature(signature.Algorithm, signature.Value, signature.CertificateThumbprint);
        }

        if (capabilities.TransmissionDeadline is { } window)
        {
            document.SetTransmissionDeadline(context.IssuedAt.Add(window));
        }

        FiscalTransmissionResult? transmission = null;

        // Clearance blocks issuance; reporting does not. This single branch is the
        // entire behavioural difference between an Italian B2B invoice and a Saudi
        // simplified receipt, and it is driven by data rather than by a country check.
        if (capabilities.TransmissionModel == TransmissionModel.Clearance
            && !context.IsOffline
            && profile.Transmitter is { } clearingTransmitter)
        {
            var result = await clearingTransmitter.TransmitAsync(
                document.Id, payload, signature, context, ct);

            if (result.IsFailure)
            {
                return Result<FiscalDocument>.Failure(result.Error);
            }

            transmission = result.Value;

            switch (transmission.Status)
            {
                case FiscalTransmissionStatus.Accepted:
                case FiscalTransmissionStatus.AcceptedWithWarnings:
                    document.MarkAccepted(transmission.AuthorityIdentifier, clock.UtcNow);
                    break;

                case FiscalTransmissionStatus.Rejected:
                    // Under clearance the document never legally existed, so the sale
                    // must not complete. Surfacing the authority's own message matters:
                    // "buyer VAT number invalid" is actionable at the till, whereas a
                    // generic failure sends the cashier to the support line.
                    document.MarkRejected();
                    return Result<FiscalDocument>.Failure(
                        FiscalErrors.ClearanceRejected(transmission.Messages));

                default:
                    document.MarkPending(clock.UtcNow);
                    break;
            }
        }

        if (capabilities.RequiresQrCode && profile.QrGenerator is { } qr)
        {
            var qrResult = qr.Generate(context, number, signature, transmission);
            if (qrResult.IsFailure)
            {
                return Result<FiscalDocument>.Failure(qrResult.Error);
            }

            document.SetQrPayload(qrResult.Value);
        }

        await store.AddAsync(document, ct);
        return Result<FiscalDocument>.Success(document);
    }
}

/// <summary>Persistence seam for documents, kept narrow deliberately.</summary>
public interface IFiscalDocumentStore
{
    public Task AddAsync(FiscalDocument document, CancellationToken ct = default);

    /// <summary>Last canonical hash in a series, for chaining jurisdictions.</summary>
    public Task<string?> GetLastCanonicalHashAsync(
        Guid companyId, Guid terminalId, string series, CancellationToken ct = default);

    /// <summary>Documents awaiting transmission, oldest first.</summary>
    public Task<IReadOnlyList<FiscalDocument>> GetPendingTransmissionAsync(
        Guid companyId, int maxCount, CancellationToken ct = default);

    /// <summary>Documents past their statutory deadline — an operational alarm, not a queue.</summary>
    public Task<IReadOnlyList<FiscalDocument>> GetOverdueAsync(
        DateTimeOffset now, CancellationToken ct = default);
}
