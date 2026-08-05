using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.Payments.Abstractions;

/// <summary>
/// The seam every acquirer, gateway and card terminal integration implements.
/// </summary>
/// <remarks>
/// <para>
/// Structured exactly like the fiscal profile seam (ADR 031), and for the same reason:
/// the core must be able to take money without knowing whose rails it is running on.
/// Callers branch on <see cref="PaymentCapabilities"/> — data — and never on the
/// provider's name. The moment a <c>if (provider == "Adyen")</c> appears, adding the
/// next provider means editing the orchestrator, and the abstraction has failed.
/// </para>
/// <para>
/// <b><see cref="QueryAsync"/> is not optional.</b> Every other method is a thing we
/// ask the provider to do; this one is how we find out what it already did. Without it
/// an indeterminate payment can never be resolved except by a human reading a
/// settlement file the next morning, with the customer long gone. A provider that
/// cannot support it is not integrable, so it sits on the required interface rather
/// than behind a capability flag.
/// </para>
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>Stable code used to route payments and to select this provider on replay.</summary>
    public string ProviderCode { get; }

    public PaymentCapabilities Capabilities { get; }

    /// <summary>Requests approval for an amount.</summary>
    public Task<PaymentOutcome> AuthoriseAsync(PaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Takes money against a prior authorisation.</summary>
    public Task<PaymentOutcome> CaptureAsync(
        string providerReference,
        Money amount,
        CancellationToken cancellationToken);

    /// <summary>Reverses an authorisation before settlement.</summary>
    public Task<PaymentOutcome> VoidAsync(string providerReference, CancellationToken cancellationToken);

    /// <summary>Returns money to the original instrument.</summary>
    public Task<PaymentOutcome> RefundAsync(RefundRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider what became of a payment we lost track of.
    /// </summary>
    /// <remarks>
    /// Looked up by our idempotency key rather than the provider's reference, because
    /// in the case this exists to solve we never received a provider reference.
    /// </remarks>
    public Task<PaymentOutcome> QueryAsync(IdempotencyKey idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// What a provider can and cannot do, as data.
/// </summary>
/// <remarks>
/// Every one of these exists because some real provider differs. Standalone bank
/// terminals capture immediately and cannot void; gateways separate auth and capture;
/// only some support partial refunds; offline floor limits are set per merchant by the
/// acquirer, not by us.
/// </remarks>
public sealed record PaymentCapabilities
{
    /// <summary>
    /// False for the common "terminal beside the till" integration, where the money is
    /// taken the moment the customer taps and there is no separate capture step.
    /// </summary>
    public required bool SeparatesAuthAndCapture { get; init; }

    /// <summary>Whether the provider will stand behind an authorisation taken with no connectivity.</summary>
    public required bool SupportsOfflineAuthorisation { get; init; }

    /// <summary>
    /// The value above which offline authorisation is not permitted.
    /// </summary>
    /// <remarks>
    /// Null when offline is unsupported entirely. Above this limit the merchant carries
    /// the loss if the card is later declined, which is why the number is configured
    /// per merchant rather than hard-coded: it is a commercial decision, not a
    /// technical one.
    /// </remarks>
    public Money? OfflineFloorLimit { get; init; }

    public required bool SupportsPartialRefund { get; init; }

    public required bool SupportsVoid { get; init; }

    /// <summary>Whether the provider returns a reusable token for the instrument.</summary>
    public required bool SupportsTokenisation { get; init; }

    /// <summary>
    /// How long to wait before declaring an attempt indeterminate.
    /// </summary>
    /// <remarks>
    /// A property rather than a constant because a chip-and-PIN terminal legitimately
    /// takes 45 seconds while the customer finds their PIN, whereas an e-commerce
    /// gateway that has not answered in 10 is already broken. One global timeout would
    /// either abandon live transactions or hang the till.
    /// </remarks>
    public required TimeSpan AuthorisationTimeout { get; init; }
}

/// <summary>
/// A request to take money. Contains no cardholder data by construction.
/// </summary>
/// <remarks>
/// The card is read inside the P2PE terminal, which returns an encrypted blob we cannot
/// decrypt and pass straight through. There is nowhere on this type to put a PAN, which
/// is the point (ADR 045).
/// </remarks>
public sealed record PaymentRequest
{
    public required Guid PaymentId { get; init; }
    public required IdempotencyKey IdempotencyKey { get; init; }
    public required Money Amount { get; init; }
    public required Guid TerminalId { get; init; }
    public required string Reference { get; init; }

    /// <summary>Opaque P2PE payload from the card reader, if one was used.</summary>
    public byte[]? EncryptedInstrument { get; init; }

    /// <summary>True when the till has no connectivity and offline rules apply.</summary>
    public required bool TerminalIsOffline { get; init; }
}

/// <summary>A request to give money back against an existing payment.</summary>
public sealed record RefundRequest
{
    public required Guid RefundPaymentId { get; init; }
    public required IdempotencyKey IdempotencyKey { get; init; }

    /// <summary>The provider's reference for the payment being refunded.</summary>
    public required string OriginalProviderReference { get; init; }

    public required Money Amount { get; init; }
    public required string Reason { get; init; }
}

/// <summary>What the provider says happened.</summary>
public sealed record PaymentOutcome
{
    public required PaymentOutcomeStatus Status { get; init; }
    public string? ProviderReference { get; init; }
    public string? AuthorisationCode { get; init; }
    public Money? ApprovedAmount { get; init; }
    public PaymentInstrument? Instrument { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }

    /// <summary>Set when the provider authorised without contacting the scheme.</summary>
    public bool AuthorisedOffline { get; init; }
}

/// <summary>
/// The outcomes a provider may report.
/// </summary>
/// <remarks>
/// <c>Declined</c>, <c>Failed</c> and <c>Unknown</c> are three different things and are
/// kept apart deliberately. Declined means the issuer said no — ask for another card.
/// Failed means the attempt definitively did not happen — retrying is safe. Unknown
/// means we do not know, and retrying may double-charge. Providers that collapse these
/// into a single error code are the reason <see cref="IPaymentProvider.QueryAsync"/>
/// exists.
/// </remarks>
public enum PaymentOutcomeStatus
{
    Authorised = 0,
    Captured = 1,
    Declined = 2,
    Failed = 3,
    Unknown = 4,
    Voided = 5,
    Refunded = 6,

    /// <summary>The provider has no record of this idempotency key.</summary>
    /// <remarks>
    /// A clean answer, not an error: it means the request never arrived, so no money
    /// moved and the payment can safely be marked failed.
    /// </remarks>
    NotFound = 7,
}

/// <summary>Resolves a provider code to its implementation.</summary>
public interface IPaymentProviderRegistry
{
    public Result<IPaymentProvider> Resolve(string providerCode);

    public IReadOnlyCollection<string> RegisteredCodes { get; }
}

/// <summary>
/// Persistence seam for payments.
/// </summary>
/// <remarks>
/// <para>
/// One of the exceptions permitted by ADR 009's criterion, on the same grounds as
/// <c>IStockLedger</c> and <c>IFiscalDocumentStore</c>: payments are written on the
/// terminal against SQLite as well as in the cloud against SQL Server.
/// </para>
/// <para>
/// <see cref="AddAndCommitAsync"/> is named for the guarantee it must provide rather
/// than the operation it performs. The write-ahead design in ADR 042 is worthless if
/// the record is merely added to a change tracker and committed later alongside the
/// provider response — it must be durable before the network call leaves the building.
/// Naming it <c>AddAsync</c> would invite exactly that mistake.
/// </para>
/// </remarks>
public interface IPaymentStore
{
    public Task AddAndCommitAsync(Payment payment, CancellationToken cancellationToken);

    public Task UpdateAndCommitAsync(Payment payment, CancellationToken cancellationToken);

    public Task<Payment?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    public Task<Payment?> FindByIdAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken);
}
