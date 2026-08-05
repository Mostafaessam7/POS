using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.Payments.Manual;

/// <summary>
/// The standalone-bank-terminal integration: the cashier runs the card on the
/// acquirer's own device beside the till and keys the approval code into the POS.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>real</b> provider, not a stub, and it is the reference implementation
/// deliberately. It is how a large share of independent retailers actually take cards
/// today, it is often the only option available while an integrated-terminal
/// certification is pending — a process the roadmap warns takes months — and it needs
/// no credentials, no network and no certification of our own. That makes it the one
/// provider that can be written and fully tested before any commercial relationship
/// exists.
/// </para>
/// <para>
/// It also exercises the awkward corners of the abstraction rather than the comfortable
/// ones: capture is inseparable from authorisation, void is impossible, and the
/// "provider reference" is a human-entered string. A seam that only ever had a
/// well-behaved REST gateway behind it would look fine and fit nothing.
/// </para>
/// <para>
/// <b>Its real limitation, stated plainly:</b> the POS has no independent evidence that
/// the payment occurred. It is recording what a human typed. Reconciliation against the
/// acquirer's settlement file is therefore not a nicety here, it is the only control —
/// which is a strong argument for integrated terminals once certification allows.
/// </para>
/// </remarks>
public sealed class ManualCardProvider : IPaymentProvider
{
    public const string Code = "MANUAL_CARD";

    public string ProviderCode => Code;

    /// <summary>
    /// Capabilities reflect physical reality, not a config file.
    /// </summary>
    /// <remarks>
    /// Offline is supported with no floor limit because our software makes no network
    /// call in either case — the bank's terminal does, on its own connection. Our
    /// connectivity is irrelevant to whether the money moves, so gating on it would
    /// block a payment that is already complete. The floor-limit risk sits with the
    /// acquirer's device, where it belongs.
    /// </remarks>
    public PaymentCapabilities Capabilities { get; } = new()
    {
        SeparatesAuthAndCapture = false,
        SupportsOfflineAuthorisation = true,
        OfflineFloorLimit = null,
        SupportsPartialRefund = true,
        SupportsVoid = false,
        SupportsTokenisation = false,
        AuthorisationTimeout = TimeSpan.FromMinutes(5),
    };

    private readonly IManualApprovalPrompt _prompt;

    public ManualCardProvider(IManualApprovalPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _prompt = prompt;
    }

    public async Task<PaymentOutcome> AuthoriseAsync(
        PaymentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var approval = await _prompt
            .PromptAsync(request.Amount, cancellationToken)
            .ConfigureAwait(false);

        if (!approval.Approved)
        {
            return new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Declined,
                Code = "manual.declined",
                Message = approval.Note ?? "The bank terminal declined the card.",
            };
        }

        // Captured, not Authorised: the money has already moved on the bank's device.
        // Reporting an authorisation would invite a capture call that cannot exist.
        return new PaymentOutcome
        {
            Status = PaymentOutcomeStatus.Captured,
            ProviderReference = approval.ApprovalCode,
            AuthorisationCode = approval.ApprovalCode,
            ApprovedAmount = request.Amount,
            AuthorisedOffline = request.TerminalIsOffline,
            Instrument = approval.MaskedPan is null
                ? null
                : new PaymentInstrument(
                    approval.MaskedPan,
                    approval.Scheme,
                    CardEntryMode.Unknown,
                    Token: null),
        };
    }

    /// <summary>Not reachable: this provider never reports an authorisation to capture.</summary>
    public Task<PaymentOutcome> CaptureAsync(
        string providerReference,
        Money amount,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentOutcome
        {
            Status = PaymentOutcomeStatus.Failed,
            Code = "manual.capture_unsupported",
            Message = "This provider captures at the point of authorisation.",
        });

    /// <summary>
    /// Unsupported. A void would have to happen on the bank's device, and claiming
    /// success here would record a reversal that never occurred.
    /// </summary>
    public Task<PaymentOutcome> VoidAsync(
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentOutcome
        {
            Status = PaymentOutcomeStatus.Failed,
            Code = "manual.void_unsupported",
            Message = "Reverse this payment on the bank terminal, then refund it here.",
        });

    public async Task<PaymentOutcome> RefundAsync(
        RefundRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var approval = await _prompt
            .PromptRefundAsync(request.Amount, cancellationToken)
            .ConfigureAwait(false);

        return approval.Approved
            ? new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Refunded,
                ProviderReference = approval.ApprovalCode,
                ApprovedAmount = request.Amount,
            }
            : new PaymentOutcome
            {
                Status = PaymentOutcomeStatus.Declined,
                Code = "manual.refund_declined",
                Message = approval.Note ?? "The refund was not completed on the bank terminal.",
            };
    }

    /// <summary>
    /// Always <see cref="PaymentOutcomeStatus.Unknown"/> — and that is the correct answer.
    /// </summary>
    /// <remarks>
    /// There is no system to query. Only a human can inspect the bank terminal's
    /// receipt roll. Returning <c>NotFound</c> would be a lie that lets the orchestrator
    /// mark the payment definitively failed and invite a retry that could double-charge;
    /// <c>Unknown</c> keeps it in the exceptions queue where a person will look at it.
    /// Honest ignorance beats a convenient answer.
    /// </remarks>
    public Task<PaymentOutcome> QueryAsync(
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentOutcome
        {
            Status = PaymentOutcomeStatus.Unknown,
            Message = "This provider cannot be queried. Check the bank terminal receipt.",
        });
}

/// <summary>
/// How the POS asks the cashier what the bank terminal said.
/// </summary>
/// <remarks>
/// An interface so the provider stays testable and headless. The terminal UI implements
/// it; tests supply canned answers.
/// </remarks>
public interface IManualApprovalPrompt
{
    public Task<ManualApproval> PromptAsync(Money amount, CancellationToken cancellationToken);

    public Task<ManualApproval> PromptRefundAsync(Money amount, CancellationToken cancellationToken);
}

/// <summary>What the cashier read off the bank terminal.</summary>
/// <remarks>
/// <see cref="MaskedPan"/> is the last four digits printed on the merchant receipt.
/// There is nowhere here to type a full card number, by design (ADR 045).
/// </remarks>
public sealed record ManualApproval(
    bool Approved,
    string? ApprovalCode,
    string? MaskedPan = null,
    string? Scheme = null,
    string? Note = null);

/// <summary>Resolves provider codes to registered providers.</summary>
/// <remarks>
/// A failure to resolve is an error, never a fallback to some default provider. Routing
/// a payment to a different acquirer than the one it was quoted against would settle
/// money into the wrong merchant account — a silent commercial error far worse than a
/// declined sale.
/// </remarks>
public sealed class PaymentProviderRegistry : IPaymentProviderRegistry
{
    private readonly Dictionary<string, IPaymentProvider> _providers;

    public PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToDictionary(
            p => p.ProviderCode,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> RegisteredCodes => _providers.Keys;

    public Result<IPaymentProvider> Resolve(string providerCode)
    {
        if (string.IsNullOrWhiteSpace(providerCode)
            || !_providers.TryGetValue(providerCode, out var provider))
        {
            return Result<IPaymentProvider>.Failure(
                PaymentErrors.UnknownProvider(providerCode ?? "(none)"));
        }

        return Result<IPaymentProvider>.Success(provider);
    }
}
