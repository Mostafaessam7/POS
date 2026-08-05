using POS.Inventory.Domain;
using POS.SharedKernel;

namespace POS.Inventory;

/// <summary>
/// The variance write-off approval ladder for this deployment.
/// </summary>
/// <remarks>
/// THIS IS DEPLOYMENT-WIDE CONFIGURATION STANDING IN FOR TENANT CONFIGURATION, the same
/// stance <c>PurchasingPolicyOptions</c> takes and for the same reason: a merchant with
/// one back room and a merchant with two hundred branches have genuinely different
/// answers to "how much shrinkage needs a director's signature", and both are right.
/// When a tenant settings store exists, the change is to resolve
/// <see cref="VarianceApprovalPolicyFor"/> per tenant; the one caller
/// (<c>StockTransferService.WriteOffVarianceAsync</c>) already goes through it.
/// </remarks>
public sealed class InventoryPolicyOptions
{
    public const string SectionName = "Inventory";

    /// <summary>
    /// Whether the person who received the transfer may also write off its variance.
    /// </summary>
    /// <remarks>Defaults to false; see <see cref="VarianceApprovalPolicy.AllowSelfApproval"/>.</remarks>
    public bool AllowSelfApproval { get; init; }

    /// <summary>Value at which each approval level becomes the minimum required to write off a variance.</summary>
    public IReadOnlyList<VarianceApprovalThresholdOption> VarianceWriteOffThresholds { get; init; } =
    [
        new() { FromValue = 0m, Level = ApprovalLevel.Supervisor },
        new() { FromValue = 500m, Level = ApprovalLevel.Manager },
        new() { FromValue = 5_000m, Level = ApprovalLevel.Director }
    ];

    /// <summary>Builds the variance approval ladder in a specific currency.</summary>
    /// <remarks>
    /// Currency comes from the variance's cost lookup, not from configuration — the same
    /// reason <c>PurchasingPolicyOptions.ApprovalPolicyFor</c> takes a currency
    /// parameter rather than storing one: a merchant trading in two currencies has one
    /// ladder whose thresholds are expressed in whichever currency the stock at that
    /// warehouse is costed in.
    /// </remarks>
    public VarianceApprovalPolicy VarianceApprovalPolicyFor(string currency) =>
        new(
            VarianceWriteOffThresholds.Select(t => new VarianceApprovalThreshold(new Money(t.FromValue, currency), t.Level)),
            AllowSelfApproval);
}

public sealed class VarianceApprovalThresholdOption
{
    public decimal FromValue { get; init; }

    public ApprovalLevel Level { get; init; }
}
