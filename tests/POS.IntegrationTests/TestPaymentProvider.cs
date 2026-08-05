using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.SharedKernel;

namespace POS.IntegrationTests;

/// <summary>
/// A payment provider stand-in whose answers the tests control.
/// </summary>
/// <remarks>
/// The estate registers no real acquirer — that is a deployment decision the platform
/// deliberately leaves open — so nothing can be resolved through the provider seam in a
/// test without a double. This one answers only to its own <see cref="ProviderCode"/>,
/// so registering it in the shared test host is inert for every path except the ones
/// that opt in by using that code.
///
/// Only <see cref="QueryAsync"/> has behaviour, because the indeterminate-payment sweep
/// is the only thing that calls a provider in these tests. The rest throw: if a test
/// ever reaches them, that is a surprise worth surfacing rather than a silent default.
/// </remarks>
public sealed class TestPaymentProvider : IPaymentProvider
{
    public const string Code = "TEST_PROVIDER";

    /// <summary>What the next <see cref="QueryAsync"/> will report. Defaults to a definite negative.</summary>
    public static PaymentOutcomeStatus NextQueryStatus { get; set; } = PaymentOutcomeStatus.NotFound;

    public string ProviderCode => Code;

    public PaymentCapabilities Capabilities { get; } = new()
    {
        SeparatesAuthAndCapture = false,
        SupportsOfflineAuthorisation = true,
        SupportsPartialRefund = true,
        SupportsVoid = true,
        SupportsTokenisation = false,
        AuthorisationTimeout = TimeSpan.FromSeconds(30)
    };

    public Task<PaymentOutcome> QueryAsync(IdempotencyKey idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentOutcome
        {
            Status = NextQueryStatus,
            Code = "test",
            Message = "Test provider query result."
        });

    public Task<PaymentOutcome> AuthoriseAsync(PaymentRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The test provider only answers QueryAsync.");

    public Task<PaymentOutcome> CaptureAsync(
        string providerReference, Money amount, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The test provider only answers QueryAsync.");

    public Task<PaymentOutcome> VoidAsync(string providerReference, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The test provider only answers QueryAsync.");

    public Task<PaymentOutcome> RefundAsync(RefundRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The test provider only answers QueryAsync.");
}
