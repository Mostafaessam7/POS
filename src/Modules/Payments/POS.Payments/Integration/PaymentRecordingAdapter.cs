using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POS.Common.Persistence;
using POS.Contracts.Payments;
using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.Payments.Persistence;
using POS.SharedKernel;

namespace POS.Payments.Integration;

/// <summary>
/// Records a sale's electronic tenders as captured payments, behind
/// <see cref="IPaymentRecordingPort"/>.
/// </summary>
/// <remarks>
/// THE ANTI-CORRUPTION LAYER for payment recording. It turns a tender — a Sales concept —
/// into a <see cref="Payment"/> in the state an offline capture leaves it: authorised
/// offline, then captured. There is no provider call, because the money already moved at
/// the terminal; recording it as anything less than captured would misrepresent settled
/// cash as still owing.
///
/// Each tender is written in its own transaction and committed independently. One tender
/// failing to record must not roll back the ones already written for the same sale: a
/// partially-recorded sale is a reconciliation finding the Sale ↔ Payment report will
/// surface, whereas rolling back would discard payments that genuinely happened.
/// </remarks>
public sealed class PaymentRecordingAdapter(
    IPaymentStore store,
    PaymentsDbContext db,
    IClock clock,
    ILogger<PaymentRecordingAdapter> logger) : IPaymentRecordingPort
{
    public async Task<Result> RecordSaleTendersAsync(
        RecordSaleTendersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = db.CurrentTenantId;

        foreach (var tender in request.Tenders)
        {
            var result = await RecordOneAsync(tenantId, request, tender, cancellationToken);

            if (result.IsFailure)
                return result;
        }

        return Result.Success();
    }

    private async Task<Result> RecordOneAsync(
        Guid tenantId,
        RecordSaleTendersRequest request,
        RecordedTender tender,
        CancellationToken cancellationToken)
    {
        // Derived, not carried. The payload has no stable per-tender id, so the key is
        // built from the sale and the tender's position — which a replay reproduces
        // exactly. This is what the unique index on (TenantId, IdempotencyKey) enforces.
        var idempotencyKey = new IdempotencyKey(
            string.Create(CultureInfo.InvariantCulture, $"sale:{request.SaleId:N}:tender:{tender.Sequence}"));

        var existing = await store.FindByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken);

        if (existing is not null)
            return Result.Success();

        var now = clock.UtcNow;

        var payment = Payment.Initiate(
            Guid.CreateVersion7(),
            tenantId,
            request.BranchId,
            request.TerminalId,
            request.SaleId,
            PaymentKind.Sale,
            tender.Amount,
            idempotencyKey,

            // We do not know which acquirer the terminal used, only the tender method.
            // Recording that is honest and queryable; inventing a provider code would
            // not be.
            $"TERMINAL_{tender.Method}".ToUpperInvariant(),
            now,
            BusinessDate.Open(request.BusinessDate));

        // Authorised offline, then captured — the two facts an offline tender carries.
        // The reference is the terminal's, which is all we have to tie this back to a
        // future settlement file.
        var reference = tender.Reference ?? idempotencyKey.Value;

        var authorised = payment.MarkAuthorised(
            reference, authorisationCode: null, instrument: null, tender.TakenAt, authorisedOffline: true);

        if (authorised.IsFailure)
            return authorised;

        var captured = payment.MarkCaptured(tender.Amount, reference, tender.TakenAt);

        if (captured.IsFailure)
            return captured;

        try
        {
            await store.AddAndCommitAsync(payment, cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
        {
            // A concurrent replay recorded this same tender first. The constraint did
            // its job; the payment exists, so this is success.
            db.ChangeTracker.Clear();

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Tender {Sequence} of sale {SaleId} was already recorded concurrently; treated as done.",
                    tender.Sequence,
                    request.SaleId);
            }
        }

        return Result.Success();
    }
}
