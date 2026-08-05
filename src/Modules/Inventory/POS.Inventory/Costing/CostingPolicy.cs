using POS.Inventory.Domain;
using POS.SharedKernel;

namespace POS.Inventory.Costing;

/// <summary>
/// Determines the cost at which a movement is valued.
/// </summary>
/// <remarks>
/// One implementation exists: weighted average, confirmed as the platform default by
/// the product owner (ADR 020). The abstraction is retained — not because a second
/// implementation is planned, but because costing method is jurisdiction-driven and a
/// future FIFO requirement must be confined to this module rather than spreading into
/// the ledger. No FIFO implementation is written speculatively; building the second
/// implementation of an unused abstraction is waste.
/// </remarks>
public interface ICostingPolicy
{
    public CostingMethod Method { get; }

    /// <summary>The cost at which an outbound movement should be valued.</summary>
    public Money CostOutbound(StockBalance balance, decimal quantity);

    /// <summary>The resulting average after an inbound movement.</summary>
    public Money RecalculateAverage(StockBalance balance, decimal incomingQuantity, Money incomingUnitCost);
}

public enum CostingMethod
{
    WeightedAverage = 0,
    Fifo = 1,
    StandardCost = 2
}

/// <summary>
/// Weighted average cost. The platform default (ADR 020).
/// </summary>
/// <remarks>
/// Chosen over FIFO because it is the most widely acceptable method across
/// jurisdictions and, importantly for this architecture, because it is far simpler to
/// compute correctly under concurrent receipts. FIFO requires maintaining ordered cost
/// layers and consuming them in sequence, which is a serialisation point on every
/// outbound movement — precisely the contention ADR 026 works to keep off the checkout
/// path. Weighted average needs a lock only on inbound movements.
/// </remarks>
public sealed class WeightedAverageCostingPolicy : ICostingPolicy
{
    public CostingMethod Method => CostingMethod.WeightedAverage;

    public Money CostOutbound(StockBalance balance, decimal quantity) => balance.AverageUnitCost;

    public Money RecalculateAverage(StockBalance balance, decimal incomingQuantity, Money incomingUnitCost)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(incomingQuantity, 0m);

        var newQuantity = balance.QuantityOnHand + incomingQuantity;

        // Negative or zero existing stock has no meaningful value to blend against —
        // see StockBalance.ApplyInbound for the reasoning.
        if (balance.QuantityOnHand <= 0m || newQuantity <= 0m)
        {
            return incomingUnitCost;
        }

        return (balance.TotalValue + (incomingUnitCost * incomingQuantity)) / newQuantity;
    }
}

/// <summary>
/// Spreads freight, duty and handling across the lines of a receipt.
/// </summary>
/// <remarks>
/// Excluding landed costs overstates margin on every subsequent sale of the goods, so
/// they must reach the unit cost rather than being expensed separately.
///
/// The apportionment cannot use naive division. Spreading £60 across three lines gives
/// £20 exactly, but £100 across three gives £33.33 three times and loses a penny — and
/// a stock valuation that is a penny out is a stock valuation that will not reconcile.
/// <see cref="Money.Allocate(IReadOnlyList{decimal})"/> distributes the remainder by
/// largest remainder so the shares always sum to the input exactly.
/// </remarks>
public static class LandedCostApportionment
{
    public static IReadOnlyList<Money> Apportion(
        Money additionalCost,
        IReadOnlyList<ReceiptLineBasis> lines,
        ApportionmentBasis basis)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
            throw new ArgumentException("At least one line is required.", nameof(lines));

        var weights = basis switch
        {
            ApportionmentBasis.Value => lines.Select(l => l.LineValue.Amount).ToArray(),
            ApportionmentBasis.Quantity => lines.Select(l => l.Quantity).ToArray(),
            ApportionmentBasis.Weight => lines.Select(l => l.WeightKg ?? 0m).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(basis))
        };

        // A basis that carries no information — every line zero-weight — would otherwise
        // throw deep inside Allocate with an opaque message. Fall back to an even split,
        // which is the only defensible answer when the chosen basis cannot discriminate.
        if (weights.All(w => w == 0m))
        {
            return additionalCost.Allocate(lines.Count);
        }

        return additionalCost.Allocate(weights);
    }
}

public sealed record ReceiptLineBasis(Guid VariantId, decimal Quantity, Money LineValue, decimal? WeightKg = null);

public enum ApportionmentBasis
{
    /// <summary>Proportional to line value. The default and the usual accounting treatment.</summary>
    Value = 0,

    /// <summary>Proportional to units. Appropriate when items are similar.</summary>
    Quantity = 1,

    /// <summary>Proportional to weight. Appropriate for freight on heterogeneous goods.</summary>
    Weight = 2
}
