using POS.SharedKernel;

namespace POS.Payments.Domain;

/// <summary>
/// The payment module's error catalogue.
/// </summary>
/// <remarks>
/// Messages are written for a cashier standing in front of a customer, not for a
/// developer reading a log. "Payment declined by the card issuer" tells them to ask for
/// another card; "InvalidStateTransitionException" tells them to call support and hold
/// up the queue. Codes stay stable for the API contract; messages may be localised.
/// </remarks>
public static class PaymentErrors
{
    public static Error InvalidTransition(PaymentStatus from, PaymentStatus to) =>
        Error.BusinessRule(
            "payment.invalid_transition",
            $"A payment cannot go from {from} to {to}.");

    public static Error CurrencyMismatch(string expected, string actual) =>
        Error.Validation(
            "payment.currency_mismatch",
            $"This payment is in {expected}; {actual} was supplied.");

    public static readonly Error CaptureExceedsAuthorisation =
        Error.BusinessRule(
            "payment.capture_exceeds_authorisation",
            "Cannot capture more than the amount authorised.");

    public static readonly Error OnlyRefundsLinkToAnOriginal =
        Error.BusinessRule(
            "payment.not_a_refund",
            "Only a refund can be linked to an original payment.");

    public static readonly Error CannotVoidRefundedPayment =
        Error.BusinessRule(
            "payment.void_after_refund",
            "This payment has already been partly refunded and can no longer be voided. " +
            "Refund the remaining balance instead.");

    public static Error OnlyCapturedPaymentsCanBeRefunded(PaymentStatus status) =>
        Error.BusinessRule(
            "payment.not_refundable",
            $"Only a captured or settled payment can be refunded; this one is {status}.");

    public static readonly Error RefundMustBePositive =
        Error.Validation(
            "payment.refund_not_positive",
            "A refund must be for a positive amount.");

    public static Error RefundExceedsRefundable(Money refundable, Money requested) =>
        Error.BusinessRule(
            "payment.refund_exceeds_refundable",
            $"Only {refundable} remains refundable on this payment; {requested} was requested.");

    public static Error UnknownProvider(string providerCode) =>
        Error.NotFound(
            "payment.unknown_provider",
            $"No payment provider is registered under '{providerCode}'.");

    public static readonly Error OriginalPaymentNotFound =
        Error.NotFound(
            "payment.original_not_found",
            "The payment being refunded could not be found.");

    /// <summary>
    /// Raised when a payment is attempted offline that the provider will not stand
    /// behind offline.
    /// </summary>
    public static readonly Error OfflineNotSupported =
        Error.BusinessRule(
            "payment.offline_unsupported",
            "This payment method needs a connection. Take cash, or try again once the " +
            "till is back online.");

    public static Error OverOfflineFloorLimit(Money limit) =>
        Error.BusinessRule(
            "payment.over_floor_limit",
            $"Card payments over {limit} cannot be taken while the till is offline.");

    /// <summary>
    /// The prior attempt's outcome is unknown, so a retry is not safe.
    /// </summary>
    /// <remarks>
    /// This blocks the exact sequence that double-charges customers: timeout, cashier
    /// presses Pay again, second authorisation succeeds alongside a first one that also
    /// succeeded. Resolution is to query the provider, not to guess.
    /// </remarks>
    public static readonly Error PriorAttemptUnresolved =
        Error.Conflict(
            "payment.prior_attempt_unresolved",
            "The previous attempt for this payment did not complete and its outcome is " +
            "not yet known. Check the card terminal before trying again.");
}
