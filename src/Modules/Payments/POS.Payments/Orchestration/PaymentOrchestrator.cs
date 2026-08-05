using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.Payments.Orchestration;

/// <summary>
/// Coordinates a payment attempt across the store and the provider.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of this class's value is in the <b>order</b> of its operations, which is
/// why the order is spelled out here and asserted by tests rather than left to be
/// inferred:
/// </para>
/// <list type="number">
/// <item>Look for an existing payment under the same idempotency key.</item>
/// <item>Apply offline eligibility rules before anything is written.</item>
/// <item>Write the payment record and <b>commit it</b>.</item>
/// <item>Only then call the provider.</item>
/// <item>Record the outcome, treating "no answer" as unknown rather than failed.</item>
/// </list>
/// <para>
/// Steps 3 and 4 are the ones that matter. Reversing them — the natural way to write
/// this, since it avoids a wasted row when the provider declines — produces a system
/// where a crash mid-authorisation leaves money moved and no local evidence of it. That
/// state cannot be detected by any query we can run; it surfaces days later as an
/// unexplained line in a settlement file. The wasted row is a rounding error against
/// that risk (ADR 042).
/// </para>
/// </remarks>
public sealed class PaymentOrchestrator(
    IPaymentProviderRegistry providers,
    IPaymentStore store,
    IClock clock)
{
    /// <summary>
    /// Takes a payment for a sale.
    /// </summary>
    public async Task<Result<Payment>> PayAsync(
        PaymentIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // (1) Idempotency first. If the terminal is retrying after a lost response, the
        // record already exists and we must not create a second one.
        var existing = await store
            .FindByIdempotencyKeyAsync(intent.TenantId, intent.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // An unresolved prior attempt must not be retried blindly — that is exactly
            // how customers get charged twice. Force it through resolution instead.
            return existing.Status == PaymentStatus.Indeterminate
                ? Result<Payment>.Failure(PaymentErrors.PriorAttemptUnresolved)
                : Result<Payment>.Success(existing);
        }

        var providerResult = providers.Resolve(intent.ProviderCode);
        if (providerResult.IsFailure)
        {
            return Result<Payment>.Failure(providerResult.Error);
        }

        var provider = providerResult.Value;

        // (2) Offline gates, evaluated before we write anything. A payment we are not
        // allowed to attempt should not leave a record suggesting we tried.
        if (intent.TerminalIsOffline)
        {
            var gate = CheckOfflineEligibility(provider.Capabilities, intent.Amount);
            if (gate.IsFailure)
            {
                return Result<Payment>.Failure(gate.Error);
            }
        }

        var now = clock.UtcNow;

        var payment = Payment.Initiate(
            Guid.CreateVersion7(),
            intent.TenantId,
            intent.BranchId,
            intent.TerminalId,
            intent.SaleId,
            PaymentKind.Sale,
            intent.Amount,
            intent.IdempotencyKey,
            intent.ProviderCode,
            now,
            intent.BusinessDate);

        // (3) Durable BEFORE the network call. Not negotiable — see the remarks above.
        await store.AddAndCommitAsync(payment, cancellationToken).ConfigureAwait(false);

        var request = new PaymentRequest
        {
            PaymentId = payment.Id,
            IdempotencyKey = intent.IdempotencyKey,
            Amount = intent.Amount,
            TerminalId = intent.TerminalId,
            Reference = intent.Reference,
            EncryptedInstrument = intent.EncryptedInstrument,
            TerminalIsOffline = intent.TerminalIsOffline,
        };

        // (4) and (5).
        var outcome = await AttemptAsync(provider, request, payment, cancellationToken)
            .ConfigureAwait(false);

        ApplyOutcome(payment, outcome, clock.UtcNow);

        await store.UpdateAndCommitAsync(payment, cancellationToken).ConfigureAwait(false);

        return Result<Payment>.Success(payment);
    }

    /// <summary>
    /// Resolves a payment whose outcome we never learned, by asking the provider.
    /// </summary>
    /// <remarks>
    /// The only safe way out of <see cref="PaymentStatus.Indeterminate"/>. Guessing
    /// either way is a financial error: guessing failure double-charges on retry,
    /// guessing success gives away goods. A <see cref="PaymentOutcomeStatus.NotFound"/>
    /// answer is a definite negative — the provider never saw the request, so no money
    /// moved — and is the one case where we may safely mark the payment failed.
    /// </remarks>
    public async Task<Result<Payment>> ResolveAsync(
        Guid tenantId,
        Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        var payment = await store.FindByIdAsync(tenantId, paymentId, cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return Result<Payment>.Failure(PaymentErrors.OriginalPaymentNotFound);
        }

        if (payment.Status != PaymentStatus.Indeterminate)
        {
            return Result<Payment>.Success(payment);
        }

        var providerResult = providers.Resolve(payment.ProviderCode);
        if (providerResult.IsFailure)
        {
            return Result<Payment>.Failure(providerResult.Error);
        }

        PaymentOutcome outcome;
        try
        {
            outcome = await providerResult.Value
                .QueryAsync(payment.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberate: any transport failure leaves the payment
        // exactly as it was — still indeterminate, still queued for another resolution
        // attempt. Letting the exception escape would abort the sweep for every other
        // payment behind it in the queue.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            payment.RecordAttempt(clock.UtcNow, "ResolutionFailed", ex.GetType().Name);
            await store.UpdateAndCommitAsync(payment, cancellationToken).ConfigureAwait(false);
            return Result<Payment>.Success(payment);
        }

        var now = clock.UtcNow;

        if (outcome.Status == PaymentOutcomeStatus.NotFound)
        {
            payment.MarkFailed(
                "provider.no_record",
                "The provider has no record of this payment; it never reached them.",
                now);
        }
        else
        {
            ApplyOutcome(payment, outcome, now);
        }

        await store.UpdateAndCommitAsync(payment, cancellationToken).ConfigureAwait(false);
        return Result<Payment>.Success(payment);
    }

    /// <summary>
    /// Issues a refund as a new payment linked to the original.
    /// </summary>
    /// <remarks>
    /// A refund is a separate <see cref="Payment"/> of kind
    /// <see cref="PaymentKind.Refund"/>, never a mutation of the original. The original
    /// is a historical fact about money that moved; editing it would destroy the record
    /// of what the customer was actually charged, which is the record a chargeback is
    /// argued from (ADR 041, and the same immutability rule as ADR 007 for sales).
    /// </remarks>
    public async Task<Result<Payment>> RefundAsync(
        RefundIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var existing = await store
            .FindByIdempotencyKeyAsync(intent.TenantId, intent.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<Payment>.Success(existing);
        }

        var original = await store
            .FindByIdAsync(intent.TenantId, intent.OriginalPaymentId, cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            return Result<Payment>.Failure(PaymentErrors.OriginalPaymentNotFound);
        }

        // Validate against the original BEFORE writing anything, so an over-refund
        // leaves no trace and no cleanup.
        var registration = original.RegisterRefund(intent.Amount);
        if (registration.IsFailure)
        {
            return Result<Payment>.Failure(registration.Error);
        }

        var providerResult = providers.Resolve(original.ProviderCode);
        if (providerResult.IsFailure)
        {
            return Result<Payment>.Failure(providerResult.Error);
        }

        var provider = providerResult.Value;

        if (intent.Amount.Amount < original.CapturedAmount.Amount
            && !provider.Capabilities.SupportsPartialRefund)
        {
            return Result<Payment>.Failure(Error.BusinessRule(
                "payment.partial_refund_unsupported",
                "This payment provider cannot refund part of a payment."));
        }

        var now = clock.UtcNow;

        var refund = Payment.Initiate(
            Guid.CreateVersion7(),
            intent.TenantId,
            original.BranchId,
            intent.TerminalId,
            original.SaleId,
            PaymentKind.Refund,
            intent.Amount,
            intent.IdempotencyKey,
            original.ProviderCode,
            now,
            intent.BusinessDate);

        refund.LinkToOriginal(original.Id);

        await store.AddAndCommitAsync(refund, cancellationToken).ConfigureAwait(false);

        var request = new RefundRequest
        {
            RefundPaymentId = refund.Id,
            IdempotencyKey = intent.IdempotencyKey,
            OriginalProviderReference = original.ProviderReference ?? string.Empty,
            Amount = intent.Amount,
            Reason = intent.Reason,
        };

        PaymentOutcome outcome;
        try
        {
            outcome = await provider.RefundAsync(request, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // See AttemptAsync: an unknown transport failure is an
        // unknown OUTCOME, and must not be recorded as a definite failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            outcome = new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Unknown,
                Message = ex.GetType().Name,
            };
        }

        ApplyOutcome(refund, outcome, clock.UtcNow);

        await store.UpdateAndCommitAsync(refund, cancellationToken).ConfigureAwait(false);

        // The original's accumulated refund total is only durable now. If this commit
        // fails the refund exists and the original under-reports what has been given
        // back — caught by the Sale/Payment reconciliation report, which is why that
        // report is mandatory.
        await store.UpdateAndCommitAsync(original, cancellationToken).ConfigureAwait(false);

        return Result<Payment>.Success(refund);
    }

    private static async Task<PaymentOutcome> AttemptAsync(
        IPaymentProvider provider,
        PaymentRequest request,
        Payment payment,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.AuthoriseAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is NOT a clean failure. The request may already be at the
            // acquirer; we simply stopped waiting for the answer.
            return new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Unknown,
                Message = "The payment was cancelled while waiting for the provider.",
            };
        }
#pragma warning disable CA1031 // Deliberate and load-bearing. A transport-level
        // exception tells us the RESPONSE failed, never that the REQUEST did. Catching
        // narrowly and letting an unfamiliar exception type escape would default this
        // to "failed" at the caller, which is the double-charge path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            payment.RecordAttempt(DateTimeOffset.UnixEpoch, "TransportFailure", ex.GetType().Name);

            return new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Unknown,
                Message = $"No response from the payment provider ({ex.GetType().Name}).",
            };
        }
    }

    private static void ApplyOutcome(Payment payment, PaymentOutcome outcome, DateTimeOffset at)
    {
        switch (outcome.Status)
        {
            case PaymentOutcomeStatus.Authorised:
                payment.MarkAuthorised(
                    outcome.ProviderReference ?? string.Empty,
                    outcome.AuthorisationCode,
                    outcome.Instrument,
                    at,
                    outcome.AuthorisedOffline);
                break;

            case PaymentOutcomeStatus.Captured:
            case PaymentOutcomeStatus.Refunded:
                payment.MarkCaptured(
                    outcome.ApprovedAmount ?? payment.Amount,
                    outcome.ProviderReference ?? string.Empty,
                    at,
                    outcome.Instrument);
                break;

            case PaymentOutcomeStatus.Declined:
                payment.MarkDeclined(
                    outcome.Code ?? "declined",
                    outcome.Message ?? "The card issuer declined this payment.",
                    at);
                break;

            case PaymentOutcomeStatus.Voided:
                payment.Void(at, outcome.Message ?? "Voided by provider.");
                break;

            case PaymentOutcomeStatus.Failed:
                payment.MarkFailed(
                    outcome.Code ?? "failed",
                    outcome.Message ?? "The payment did not complete.",
                    at);
                break;

            case PaymentOutcomeStatus.NotFound:
            case PaymentOutcomeStatus.Unknown:
            default:
                payment.MarkIndeterminate(
                    outcome.Message ?? "The provider did not respond.",
                    at);
                break;
        }
    }

    private static Result CheckOfflineEligibility(PaymentCapabilities capabilities, Money amount)
    {
        if (!capabilities.SupportsOfflineAuthorisation)
        {
            return Result.Failure(PaymentErrors.OfflineNotSupported);
        }

        var limit = capabilities.OfflineFloorLimit;

        if (limit is not null
            && limit.Value.Currency == amount.Currency
            && amount.Amount > limit.Value.Amount)
        {
            return Result.Failure(PaymentErrors.OverOfflineFloorLimit(limit.Value));
        }

        return Result.Success();
    }
}

/// <summary>A request from the till to take money.</summary>
public sealed record PaymentIntent
{
    public required Guid TenantId { get; init; }
    public required Guid BranchId { get; init; }
    public required Guid TerminalId { get; init; }
    public required Guid SaleId { get; init; }
    public required Money Amount { get; init; }
    public required IdempotencyKey IdempotencyKey { get; init; }
    public required string ProviderCode { get; init; }
    public required string Reference { get; init; }
    public required BusinessDate BusinessDate { get; init; }
    public required bool TerminalIsOffline { get; init; }
    public byte[]? EncryptedInstrument { get; init; }
}

/// <summary>A request from the till to give money back.</summary>
public sealed record RefundIntent
{
    public required Guid TenantId { get; init; }
    public required Guid TerminalId { get; init; }
    public required Guid OriginalPaymentId { get; init; }
    public required Money Amount { get; init; }
    public required IdempotencyKey IdempotencyKey { get; init; }
    public required string Reason { get; init; }
    public required BusinessDate BusinessDate { get; init; }
}
