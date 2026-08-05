using POS.Purchasing.Domain;
using POS.SharedKernel;

namespace POS.Purchasing;

/// <summary>
/// Approval thresholds and receipt tolerances for this deployment.
/// </summary>
/// <remarks>
/// THIS IS DEPLOYMENT-WIDE CONFIGURATION STANDING IN FOR TENANT CONFIGURATION, and the
/// distinction matters enough to state plainly. ADR 049 has these as per-tenant
/// settings — one merchant approves anything above £500, another above £50,000 — and
/// they should ultimately be read from a tenant settings store. No such store exists.
///
/// Binding them from configuration is chosen over the alternative of hard-coding a
/// policy inline at each call site, which is what the walking-skeleton host does and
/// which makes the numbers invisible and untestable. When tenant settings arrive, the
/// change is to resolve <see cref="ApprovalPolicyFor"/> per tenant; every caller
/// already goes through it.
/// </remarks>
public sealed class PurchasingPolicyOptions
{
    public const string SectionName = "Purchasing";

    /// <summary>Orders at or below this value need no approval.</summary>
    public decimal ApprovalRequiredAbove { get; init; } = 1_000m;

    /// <summary>
    /// Whether the person who raised an order may approve it.
    /// </summary>
    /// <remarks>
    /// Defaults to false, and should stay false. Self-approval is the single control
    /// that separation of duties exists to provide (ADR 050); the flag exists because a
    /// sole trader running the whole shop has nobody else to ask, not because it is a
    /// preference.
    /// </remarks>
    public bool AllowSelfApproval { get; init; }

    /// <summary>Value at which each approval level becomes the minimum required.</summary>
    public IReadOnlyList<ApprovalThresholdOption> Thresholds { get; init; } =
    [
        new() { FromValue = 1_000m, Level = ApprovalLevel.Supervisor },
        new() { FromValue = 10_000m, Level = ApprovalLevel.Manager },
        new() { FromValue = 50_000m, Level = ApprovalLevel.Director }
    ];

    /// <summary>Over-receipt allowed as a percentage of the ordered quantity.</summary>
    public decimal ReceiptTolerancePercentage { get; init; } = 5m;

    /// <summary>Over-receipt allowed in absolute units, whichever is greater.</summary>
    public decimal ReceiptToleranceUnits { get; init; } = 2m;

    /// <summary>Builds the approval ladder in a specific currency.</summary>
    /// <remarks>
    /// Currency comes from the ORDER, not from configuration: a merchant trading in two
    /// currencies has one approval ladder whose thresholds are expressed in whichever
    /// currency the document is denominated in. Storing a currency here would silently
    /// compare pounds against euros the first time somebody raised a foreign order.
    /// </remarks>
    public ApprovalPolicy ApprovalPolicyFor(string currency) =>
        new(new Money(ApprovalRequiredAbove, currency),
            Thresholds.Select(t => new ApprovalThreshold(new Money(t.FromValue, currency), t.Level)),
            AllowSelfApproval);

    public ReceiptTolerance ReceiptTolerance() =>
        new(ReceiptTolerancePercentage, ReceiptToleranceUnits);
}

public sealed class ApprovalThresholdOption
{
    public decimal FromValue { get; init; }

    public ApprovalLevel Level { get; init; }
}
