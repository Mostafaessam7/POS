using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// A charge that forms part of the cost of getting goods onto the shelf.
/// </summary>
/// <param name="Type">What kind of charge this is — freight, duty, insurance, handling.</param>
/// <param name="Amount">The charge, in the delivery's currency.</param>
/// <param name="Reference">
/// The freight invoice or customs declaration number. Not decorative: when a cost is
/// disputed months later this is the only thread back to the paperwork.
/// </param>
/// <param name="Basis">How this charge is spread across the delivery's lines.</param>
public sealed record LandedCostCharge(
    LandedCostType Type,
    Money Amount,
    string Reference,
    LandedCostAllocationBasis Basis)
{
    /// <inheritdoc cref="GoodsReceiptLine()"/>
    private LandedCostCharge()
        : this(LandedCostType.Freight, default, string.Empty, LandedCostAllocationBasis.Value)
    {
    }
}

public enum LandedCostType
{
    Freight = 1,
    Duty = 2,
    Insurance = 3,
    Handling = 4,
    Other = 99
}

/// <summary>
/// How a charge is spread across the lines of a delivery.
/// </summary>
/// <remarks>
/// The basis is a property of the charge, not of the delivery, because different charges
/// genuinely apportion differently and using one basis for all of them is the usual
/// shortcut. Duty is levied on declared value, so it follows value. A haulier charges for
/// a pallet, so freight follows weight or volume — and where neither is known, quantity is
/// the least-wrong proxy. Spreading a container's freight by value would load it onto the
/// expensive electronics and leave the heavy, cheap goods that actually filled the
/// container looking costless.
/// </remarks>
public enum LandedCostAllocationBasis
{
    /// <summary>In proportion to line value. Correct for duty and ad-valorem charges.</summary>
    Value = 1,

    /// <summary>In proportion to units received. The default proxy for freight.</summary>
    Quantity = 2,

    /// <summary>Evenly across lines, regardless of size. For per-line administrative fees.</summary>
    Even = 3
}

/// <summary>
/// Spreads landed costs across receipt lines so that the parts sum exactly to the whole.
/// </summary>
/// <remarks>
/// A pure static function over plain data: lines and charges in, an array of allocated
/// amounts out, positionally aligned with the input lines. No clock, no database, no
/// aggregate. That is what makes the rounding behaviour — the interesting part — testable
/// in isolation.
///
/// The exact-sum property is delegated to <see cref="Money.Allocate(IReadOnlyList{decimal})"/>,
/// which distributes the indivisible remainder a minor unit at a time. Naive division
/// loses a penny per charge, and a penny lost here is a stock valuation that will not
/// reconcile against the purchase ledger — a discrepancy that takes an afternoon to find
/// and is worth one cent.
/// </remarks>
public static class LandedCostAllocator
{
    /// <summary>
    /// Allocates every charge across the lines and returns each line's total share.
    /// </summary>
    public static IReadOnlyList<Money> Allocate(
        IReadOnlyList<GoodsReceiptLine> lines,
        IReadOnlyList<LandedCostCharge> charges,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(charges);

        var totals = new Money[lines.Count];
        for (var i = 0; i < totals.Length; i++)
        {
            totals[i] = Money.Zero(currency);
        }

        if (lines.Count == 0 || charges.Count == 0)
        {
            return totals;
        }

        foreach (var charge in charges)
        {
            var weights = WeightsFor(lines, charge.Basis);

            // Every weight zero means the basis cannot discriminate between these lines —
            // a zero-value delivery of samples allocated by value, for instance. Falling
            // back to an even split keeps the money attached to the goods rather than
            // throwing away a charge that was genuinely incurred.
            if (weights.All(w => w == 0m))
            {
                weights = Enumerable.Repeat(1m, lines.Count).ToArray();
            }

            var shares = charge.Amount.Allocate(weights);

            for (var i = 0; i < lines.Count; i++)
            {
                totals[i] += shares[i];
            }
        }

        return totals;
    }

    private static decimal[] WeightsFor(IReadOnlyList<GoodsReceiptLine> lines, LandedCostAllocationBasis basis) =>
        basis switch
        {
            LandedCostAllocationBasis.Value => lines.Select(l => l.LineValue.Amount).ToArray(),
            LandedCostAllocationBasis.Quantity => lines.Select(l => l.QuantityReceived).ToArray(),
            LandedCostAllocationBasis.Even => Enumerable.Repeat(1m, lines.Count).ToArray(),
            _ => Enumerable.Repeat(1m, lines.Count).ToArray()
        };
}

/// <summary>
/// A landed cost that arrived after the goods were already received, and possibly sold.
/// </summary>
/// <remarks>
/// This is the awkward case the roadmap flags, and it has no cost-free answer. A freight
/// invoice turns up three weeks after the delivery. Some of those goods are still on the
/// shelf; some have been sold, at a margin we have already reported.
///
/// Three options, and why this one:
///
/// <list type="bullet">
/// <item><b>Restate history.</b> Reopen the sales, recompute their cost of goods, restate
/// the margin. Correct in a textbook, and forbidden here — D6 makes financial records
/// immutable, and reissuing a period that has been reported on (or filed) is exactly the
/// thing an auditor objects to.</item>
/// <item><b>Book the whole charge to variance.</b> Simple, and it understates the cost of
/// every unit still on the shelf. Those units get sold next week at a margin computed from
/// a cost we know to be wrong.</item>
/// <item><b>Split it.</b> Revalue the proportion still on hand; book the rest to
/// variance.</item>
/// </list>
///
/// The third is what this implements. The units still on hand can be corrected without
/// touching history, so they are. The units already sold cannot be, so their share is
/// recognised as a purchase price variance in the current period — which is what a
/// variance account is for, and where an accountant will expect to find it.
///
/// The split is by <em>quantity remaining</em>, not by value, because it is answering
/// "how many of the units this charge relates to do I still have". See ADR 049.
/// </remarks>
public static class LateLandedCostAllocator
{
    /// <summary>
    /// Splits a late charge into the part that revalues remaining stock and the part that
    /// must be expensed.
    /// </summary>
    /// <param name="charge">The late-arriving amount attributable to one variant.</param>
    /// <param name="quantityReceived">How many units the original receipt brought in.</param>
    /// <param name="quantityStillOnHand">
    /// How many of those remain. Supplied by the caller from the stock balance rather than
    /// derived here, because Purchasing does not read Inventory's tables (ADR 002).
    /// </param>
    public static LateLandedCostSplit Split(Money charge, decimal quantityReceived, decimal quantityStillOnHand)
    {
        if (quantityReceived <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityReceived), "The original receipt must have brought in units.");
        }

        if (quantityStillOnHand < 0m)
        {
            // Negative stock is permitted by ADR 027, but it cannot be revalued: there are
            // no units present to carry the cost. Treated as nothing on hand, so the whole
            // charge is expensed — and the variance is where someone will notice it.
            quantityStillOnHand = 0m;
        }

        // Cap at the received quantity. More on hand than we received means later receipts
        // have arrived, and their units did not incur this charge.
        var attributable = Math.Min(quantityStillOnHand, quantityReceived);

        if (attributable == 0m)
        {
            return new LateLandedCostSplit(Money.Zero(charge.Currency), charge, 0m);
        }

        if (attributable == quantityReceived)
        {
            return new LateLandedCostSplit(charge, Money.Zero(charge.Currency), 1m);
        }

        var proportion = attributable / quantityReceived;

        // Allocate rather than multiply, so the two halves sum exactly to the charge.
        // Multiplying and subtracting would be equivalent here, but only by accident of
        // this being a two-way split; Allocate stays correct if a third bucket is ever
        // added, and it rounds the same way as every other split in the system.
        var shares = charge.Allocate([attributable, quantityReceived - attributable]);

        return new LateLandedCostSplit(shares[0], shares[1], proportion);
    }
}

/// <param name="Revaluation">
/// Applied to the stock ledger as a value-only <c>CostAdjustment</c> movement — quantity
/// unchanged, value corrected.
/// </param>
/// <param name="Variance">
/// The share relating to units already sold. Recognised in the current period; there is no
/// General Ledger module yet, so this is carried on the document for later export.
/// </param>
/// <param name="ProportionOnHand">The fraction driving the split, retained so the figure can be explained.</param>
public sealed record LateLandedCostSplit(
    Money Revaluation,
    Money Variance,
    decimal ProportionOnHand);
