using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// Approval seniority for writing off a transfer's variance. Ordered, so a higher level
/// satisfies a lower requirement.
/// </summary>
/// <remarks>
/// Deliberately a separate enum from Purchasing's <c>ApprovalLevel</c> rather than a
/// shared one, even though the values line up — Inventory must not take a dependency on
/// Purchasing's domain assembly to express "how senior", or the module boundary
/// (ADR 002) would be a name away from meaningless. Two modules independently needing
/// the same shape is not evidence they should share a type.
/// </remarks>
public enum ApprovalLevel
{
    None = 0,
    Supervisor = 1,
    Manager = 2,
    Director = 3
}

/// <summary>
/// Tenant-configured thresholds deciding who may write off how much variance.
/// </summary>
/// <remarks>
/// Same shape as Purchasing's <c>ApprovalPolicy</c>, for the same reason: a merchant
/// with one back-room and a merchant with two hundred branches have genuinely different
/// answers to "how much shrinkage needs a director's signature", and hard-coding either
/// produces a branch on tenant identity. See <c>InventoryPolicyOptions</c> for why this
/// is deployment configuration standing in for tenant configuration, not tenant data
/// yet.
/// </remarks>
public sealed class VarianceApprovalPolicy
{
    private readonly List<VarianceApprovalThreshold> _thresholds;

    public VarianceApprovalPolicy(IEnumerable<VarianceApprovalThreshold> thresholds, bool allowSelfApproval = false)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        AllowSelfApproval = allowSelfApproval;
        _thresholds = thresholds.OrderBy(t => t.FromValue.Amount).ToList();
    }

    /// <summary>
    /// Whether the person who received the transfer (and so found the variance) may
    /// also be the one who writes it off.
    /// </summary>
    /// <remarks>
    /// Defaults to false and should stay false in any tenant with more than one member
    /// of staff — the entire point of the control is that the person who discovered a
    /// shortfall is not also the one who gets to decide, unchecked, that it is written
    /// off rather than investigated. Configurable at all only for the sole-trader case
    /// that has nobody else to ask, the same reasoning Purchasing's
    /// <c>AllowSelfApproval</c> uses (ADR 050).
    /// </remarks>
    public bool AllowSelfApproval { get; }

    public IReadOnlyList<VarianceApprovalThreshold> Thresholds => _thresholds;

    /// <summary>The minimum seniority that may write off a variance of this value.</summary>
    public ApprovalLevel RequiredLevel(Money varianceValue)
    {
        var level = ApprovalLevel.Supervisor;

        foreach (var threshold in _thresholds)
        {
            if (varianceValue >= threshold.FromValue)
            {
                level = threshold.Level;
            }
        }

        return level;
    }

    /// <summary>A policy where any holder of the base permission may write off any amount — for tests.</summary>
    public static VarianceApprovalPolicy None() => new([], allowSelfApproval: true);
}

public sealed record VarianceApprovalThreshold(Money FromValue, ApprovalLevel Level);
