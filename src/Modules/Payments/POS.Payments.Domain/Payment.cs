using POS.SharedKernel;

namespace POS.Payments.Domain;

/// <summary>
/// A single movement of money through a payment instrument, tracked independently of
/// the sale it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not part of <c>Sale</c>.</b> A tender line on a sale records the
/// merchant's intent — "the customer is paying 40.00 by card". A payment records what
/// an external system actually did with the money, and the two disagree more often
/// than is comfortable: the card authorises but the sale is abandoned, the sale
/// completes offline but the payment settles three days later, a refund is issued
/// against a sale that has already been archived. Modelling the payment inside the sale
/// aggregate would force the sale's lifetime and consistency boundary onto an object
/// whose lifetime is controlled by an acquirer. Same reasoning as
/// <c>FiscalDocument</c> (ADR 033), and the same cost: the database cannot enforce the
/// relationship, so a reconciliation report becomes mandatory rather than optional.
/// </para>
/// <para>
/// <b>Cardholder data.</b> There is deliberately no PAN, no CVV, no expiry date and no
/// track data on this type, or anywhere else in this codebase. Under P2PE the card is
/// read and encrypted inside a certified terminal and our software never sees the
/// clear value. This is not defensive coding; it is the difference between PCI-DSS SAQ
/// P2PE and a full Report on Compliance (ADR 045). An architecture test fails the build
/// if such a field appears.
/// </para>
/// </remarks>
public sealed class Payment : AggregateRoot<Guid>, ITenantScoped, IBranchScoped
{
    private readonly List<PaymentAttempt> _attempts = [];

    private Payment() { }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid TerminalId { get; private set; }

    /// <summary>The sale being paid for. A loose reference — no foreign key.</summary>
    public Guid SaleId { get; private set; }

    public PaymentKind Kind { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>The amount requested. Never mutated — a partial capture is recorded separately.</summary>
    public Money Amount { get; private set; }

    /// <summary>
    /// What the provider actually captured, which may be less than <see cref="Amount"/>.
    /// </summary>
    public Money CapturedAmount { get; private set; }

    /// <summary>Cumulative amount refunded against this payment.</summary>
    public Money RefundedAmount { get; private set; }

    /// <summary>
    /// The client-generated key that makes retrying this payment safe.
    /// </summary>
    /// <remarks>
    /// Generated on the terminal before the first attempt, not by the server. A
    /// server-generated key cannot protect the case that actually causes double
    /// charges: the request reaches the provider, the response is lost, and the
    /// cashier presses Pay again. See ADR 043.
    /// </remarks>
    public IdempotencyKey IdempotencyKey { get; private set; } = default!;

    public string ProviderCode { get; private set; } = string.Empty;

    /// <summary>The provider's own identifier, once it has given us one.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Scheme authorisation code, for dispute handling.</summary>
    public string? AuthorisationCode { get; private set; }

    public PaymentInstrument? Instrument { get; private set; }

    /// <summary>For a refund, the payment being refunded (ADR 041).</summary>
    public Guid? OriginalPaymentId { get; private set; }

    public DateTimeOffset InitiatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public BusinessDate BusinessDate { get; private set; }

    /// <summary>Whether this was authorised without contacting the acquirer.</summary>
    /// <remarks>
    /// Persisted rather than inferred. An offline authorisation carries merchant
    /// liability if it is later declined, and finance must be able to quantify that
    /// exposure without re-deriving it from connectivity logs that no longer exist.
    /// </remarks>
    public bool AuthorisedOffline { get; private set; }

    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }

    public IReadOnlyList<PaymentAttempt> Attempts => _attempts.AsReadOnly();

    /// <summary>Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Money is owed to the customer or the acquirer until this is true.</summary>
    public bool IsFinal => Status is PaymentStatus.Settled
                                  or PaymentStatus.Declined
                                  or PaymentStatus.Failed
                                  or PaymentStatus.Voided;

    /// <summary>
    /// Amount still refundable. Guards against refunding more than was taken.
    /// </summary>
    public Money RefundableAmount => CapturedAmount - RefundedAmount;

    /// <summary>
    /// Creates the payment record. This must be persisted BEFORE the provider is called.
    /// </summary>
    /// <remarks>
    /// Write-ahead, per ADR 042. If we call the provider first and crash before
    /// writing, money has moved and no record of it exists — an unrecoverable state
    /// that can only be found by reading the acquirer's settlement file. Writing first
    /// degrades the worst case to "a record exists whose outcome is unknown", which is
    /// recoverable by querying the provider. The ordering is the whole design.
    /// </remarks>
    public static Payment Initiate(
        Guid id,
        Guid tenantId,
        Guid branchId,
        Guid terminalId,
        Guid saleId,
        PaymentKind kind,
        Money amount,
        IdempotencyKey idempotencyKey,
        string providerCode,
        DateTimeOffset initiatedAt,
        BusinessDate businessDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        if (amount.Amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "A payment must be for a positive amount. A refund is a separate payment " +
                "of kind Refund, not a negative sale.");
        }

        var zero = Money.Zero(amount.Currency);

        return new Payment
        {
            Id = id,
            TenantId = tenantId,
            BranchId = branchId,
            TerminalId = terminalId,
            SaleId = saleId,
            Kind = kind,
            Status = PaymentStatus.Initiated,
            Amount = amount,
            CapturedAmount = zero,
            RefundedAmount = zero,
            IdempotencyKey = idempotencyKey,
            ProviderCode = providerCode,
            InitiatedAt = initiatedAt,
            BusinessDate = businessDate,
        };
    }

    /// <summary>Links a refund to the payment it reverses.</summary>
    public Result LinkToOriginal(Guid originalPaymentId)
    {
        if (Kind != PaymentKind.Refund)
        {
            return Result.Failure(PaymentErrors.OnlyRefundsLinkToAnOriginal);
        }

        OriginalPaymentId = originalPaymentId;
        return Result.Success();
    }

    /// <summary>
    /// Records that the provider approved the amount but has not yet taken the money.
    /// </summary>
    public Result MarkAuthorised(
        string providerReference,
        string? authorisationCode,
        PaymentInstrument? instrument,
        DateTimeOffset at,
        bool authorisedOffline = false)
    {
        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Indeterminate))
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Authorised));
        }

        Status = PaymentStatus.Authorised;
        ProviderReference = providerReference;
        AuthorisationCode = authorisationCode;
        Instrument = instrument;
        AuthorisedOffline = authorisedOffline;
        return Result.Success();
    }

    /// <summary>
    /// Records that the money has been taken.
    /// </summary>
    /// <remarks>
    /// Callable directly from <c>Initiated</c> because many providers — and every
    /// standalone bank terminal — do not separate authorisation from capture. Forcing
    /// an artificial Authorised step for those would record a state that never existed.
    /// </remarks>
    public Result MarkCaptured(
        Money capturedAmount,
        string providerReference,
        DateTimeOffset at,
        PaymentInstrument? instrument = null)
    {
        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Authorised or PaymentStatus.Indeterminate))
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Captured));
        }

        if (capturedAmount.Currency != Amount.Currency)
        {
            return Result.Failure(PaymentErrors.CurrencyMismatch(Amount.Currency, capturedAmount.Currency));
        }

        if (capturedAmount.Amount > Amount.Amount)
        {
            return Result.Failure(PaymentErrors.CaptureExceedsAuthorisation);
        }

        Status = PaymentStatus.Captured;
        CapturedAmount = capturedAmount;
        ProviderReference = providerReference;
        CompletedAt = at;

        if (instrument is not null)
        {
            Instrument = instrument;
        }

        return Result.Success();
    }

    /// <summary>Records that the acquirer has settled the funds.</summary>
    public Result MarkSettled(DateTimeOffset settledAt)
    {
        if (Status != PaymentStatus.Captured)
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Settled));
        }

        Status = PaymentStatus.Settled;
        SettledAt = settledAt;
        return Result.Success();
    }

    /// <summary>The customer's bank said no. A normal outcome, not an error.</summary>
    public Result MarkDeclined(string code, string message, DateTimeOffset at)
    {
        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Indeterminate))
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Declined));
        }

        Status = PaymentStatus.Declined;
        FailureCode = code;
        FailureMessage = message;
        CompletedAt = at;
        return Result.Success();
    }

    /// <summary>
    /// The attempt definitively did not take money.
    /// </summary>
    /// <remarks>
    /// Only for outcomes we know to be terminal. If we merely failed to hear back, the
    /// correct status is <see cref="PaymentStatus.Indeterminate"/> — see
    /// <see cref="MarkIndeterminate"/>.
    /// </remarks>
    public Result MarkFailed(string code, string message, DateTimeOffset at)
    {
        if (IsFinal)
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Failed));
        }

        Status = PaymentStatus.Failed;
        FailureCode = code;
        FailureMessage = message;
        CompletedAt = at;
        return Result.Success();
    }

    /// <summary>
    /// We do not know whether money moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most important status in the module and the one most often
    /// missing from payment systems. When a request times out, the transaction may have
    /// succeeded at the acquirer and been lost on the way back. Recording that as
    /// <c>Failed</c> tells the cashier to retry, and the customer is charged twice.
    /// Recording it as <c>Captured</c> hands over goods that were never paid for.
    /// </para>
    /// <para>
    /// The only correct response is to admit ignorance, block automatic retry, and
    /// resolve it by <b>asking the provider</b> what happened — which is why
    /// <c>QueryAsync</c> is on the required part of the provider interface rather than
    /// an optional capability. See ADR 044.
    /// </para>
    /// </remarks>
    public Result MarkIndeterminate(string reason, DateTimeOffset at)
    {
        if (IsFinal)
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Indeterminate));
        }

        Status = PaymentStatus.Indeterminate;
        FailureMessage = reason;
        _attempts.Add(new PaymentAttempt(_attempts.Count + 1, at, "Indeterminate", reason));
        return Result.Success();
    }

    /// <summary>Reverses an authorisation before settlement.</summary>
    public Result Void(DateTimeOffset at, string reason)
    {
        if (Status is not (PaymentStatus.Authorised or PaymentStatus.Captured))
        {
            return Result.Failure(PaymentErrors.InvalidTransition(Status, PaymentStatus.Voided));
        }

        if (RefundedAmount.Amount > 0m)
        {
            return Result.Failure(PaymentErrors.CannotVoidRefundedPayment);
        }

        Status = PaymentStatus.Voided;
        FailureMessage = reason;
        CompletedAt = at;
        return Result.Success();
    }

    /// <summary>
    /// Records that a refund has been issued against this payment.
    /// </summary>
    /// <remarks>
    /// Accumulates rather than replaces, because partial refunds are normal and the
    /// invariant that matters is the total: no sequence of partial refunds may exceed
    /// what was captured. Enforcing that here rather than in the orchestrator means it
    /// holds regardless of which code path issues the refund.
    /// </remarks>
    public Result RegisterRefund(Money refundAmount)
    {
        if (Status is not (PaymentStatus.Captured or PaymentStatus.Settled))
        {
            return Result.Failure(PaymentErrors.OnlyCapturedPaymentsCanBeRefunded(Status));
        }

        if (refundAmount.Currency != CapturedAmount.Currency)
        {
            return Result.Failure(PaymentErrors.CurrencyMismatch(CapturedAmount.Currency, refundAmount.Currency));
        }

        if (refundAmount.Amount <= 0m)
        {
            return Result.Failure(PaymentErrors.RefundMustBePositive);
        }

        if (refundAmount.Amount > RefundableAmount.Amount)
        {
            return Result.Failure(PaymentErrors.RefundExceedsRefundable(RefundableAmount, refundAmount));
        }

        RefundedAmount = RefundedAmount + refundAmount;
        return Result.Success();
    }

    /// <summary>Appends an attempt to the audit trail.</summary>
    public void RecordAttempt(DateTimeOffset at, string outcome, string? detail)
        => _attempts.Add(new PaymentAttempt(_attempts.Count + 1, at, outcome, detail));
}

/// <summary>Whether this payment takes money or gives it back.</summary>
public enum PaymentKind
{
    Sale = 0,
    Refund = 1,
}

/// <summary>
/// The payment lifecycle.
/// </summary>
/// <remarks>
/// <c>Indeterminate</c> is deliberately distinct from <c>Failed</c>. See
/// <see cref="Payment.MarkIndeterminate"/> for why collapsing them causes double
/// charges.
/// </remarks>
public enum PaymentStatus
{
    Initiated = 0,
    Authorised = 1,
    Captured = 2,
    Settled = 3,
    Declined = 4,
    Failed = 5,
    Indeterminate = 6,
    Voided = 7,
}

/// <summary>How the card was presented. Affects liability and dispute rights.</summary>
public enum CardEntryMode
{
    Unknown = 0,
    Chip = 1,
    Contactless = 2,
    MagneticStripe = 3,
    ManualEntry = 4,
    Ecommerce = 5,
}

/// <summary>
/// The non-sensitive residue of a card, which is all we are permitted to retain.
/// </summary>
/// <remarks>
/// <see cref="MaskedPan"/> holds the last four digits only — enough for a cashier to
/// match a receipt to a customer's statement and nothing more. There is no property
/// here for a full PAN, expiry or CVV, and adding one would fail
/// <c>CardDataArchitectureTests</c>. See ADR 045.
/// </remarks>
public sealed record PaymentInstrument(
    string MaskedPan,
    string? Scheme,
    CardEntryMode EntryMode,
    string? Token)
{
    /// <summary>Cash, vouchers and account payments have no instrument detail.</summary>
    public static PaymentInstrument None { get; } = new("", null, CardEntryMode.Unknown, null);
}

/// <summary>
/// A client-generated key identifying one payment intent across all its retries.
/// </summary>
/// <remarks>
/// A value object rather than a bare string so that "which string is this" cannot be
/// got wrong at a call site, and so the non-empty rule lives in one place. Uniqueness
/// is enforced by a unique index on (TenantId, IdempotencyKey), which is what actually
/// stops the double charge — an application-level check has a race between the lookup
/// and the insert.
/// </remarks>
public sealed record IdempotencyKey
{
    public string Value { get; }

    public IdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 100)
        {
            throw new ArgumentException("Idempotency key must be 100 characters or fewer.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Creates a key for a fresh payment intent.</summary>
    /// <remarks>
    /// Version 7 so keys sort by creation time, which makes the support query
    /// "what did this till do around 14:32" an index seek rather than a scan.
    /// </remarks>
    public static IdempotencyKey New() => new(Guid.CreateVersion7().ToString("N"));

    public override string ToString() => Value;
}

/// <summary>One attempt against the provider, kept for support and dispute evidence.</summary>
public sealed record PaymentAttempt(
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    string Outcome,
    string? Detail);
