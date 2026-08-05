using POS.Inventory.Costing;
using POS.Inventory.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class StockMovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 20);
    private static readonly StockDocumentReference Ref =
        new(StockDocumentType.Sale, Guid.CreateVersion7(), "S-001");

    private static StockMovement Movement(MovementType type, decimal delta) =>
        StockMovement.Record(
            tenantId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            variantId: Guid.CreateVersion7(),
            type: type,
            quantityDelta: delta,
            unitCost: new Money(2.50m, "GBP"),
            reference: Ref,
            occurredAt: Now,
            businessDate: Today,
            terminalId: null,
            userId: null);

    [Fact]
    public void A_sale_recorded_as_a_positive_quantity_is_rejected()
    {
        // The transposition bug: caught at creation rather than during a stocktake
        // three weeks later, when nobody can reconstruct what happened.
        Should.Throw<ArgumentException>(() => Movement(MovementType.Sale, 1m));
    }

    [Fact]
    public void A_receipt_recorded_as_a_negative_quantity_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Movement(MovementType.Receipt, -1m));
    }

    [Fact]
    public void A_zero_quantity_movement_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Movement(MovementType.Sale, 0m));
    }

    [Fact]
    public void A_stocktake_adjustment_may_go_in_either_direction()
    {
        Should.NotThrow(() => Movement(MovementType.StocktakeAdjustment, 3m));
        Should.NotThrow(() => Movement(MovementType.StocktakeAdjustment, -3m));
    }

    [Fact]
    public void Total_cost_is_the_absolute_quantity_times_unit_cost()
    {
        // Absolute, because a sale of 4 units at £2.50 cost £10 of stock — the value
        // leaving is positive even though the quantity delta is negative.
        Movement(MovementType.Sale, -4m).TotalCost.ShouldBe(new Money(10m, "GBP"));
    }

    [Fact]
    public void A_negative_unit_cost_is_rejected()
    {
        Should.Throw<ArgumentException>(() => StockMovement.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            MovementType.Receipt, 1m, new Money(-1m, "GBP"), Ref, Now, Today, null, null));
    }

    [Theory]
    [InlineData(MovementType.Receipt, true)]
    [InlineData(MovementType.CustomerReturn, true)]
    [InlineData(MovementType.TransferIn, true)]
    [InlineData(MovementType.Sale, false)]
    [InlineData(MovementType.Wastage, false)]
    [InlineData(MovementType.TransferOut, false)]
    public void Only_inbound_movements_may_change_average_cost(MovementType type, bool expected)
    {
        // The predicate that keeps checkout off the contended path (ADR 026). If a sale
        // ever starts affecting cost, throughput regresses silently — hence the test.
        type.AffectsAverageCost().ShouldBe(expected);
    }

    [Fact]
    public void Manual_movements_require_a_reason_code()
    {
        MovementType.Wastage.RequiresReasonCode().ShouldBeTrue();
        MovementType.AdjustmentDecrease.RequiresReasonCode().ShouldBeTrue();
        MovementType.Sale.RequiresReasonCode().ShouldBeFalse();
    }
}

public sealed class StockBalanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 14, 0, 0, TimeSpan.Zero);

    private static StockBalance Empty() =>
        StockBalance.Empty(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "GBP");

    [Fact]
    public void Weighted_average_blends_two_receipts()
    {
        // 10 @ £1.00 then 10 @ £2.00 = 20 @ £1.50
        var balance = Empty();

        balance.ApplyInbound(10m, new Money(1m, "GBP"), Now);
        balance.ApplyInbound(10m, new Money(2m, "GBP"), Now);

        balance.QuantityOnHand.ShouldBe(20m);
        balance.AverageUnitCost.Amount.ShouldBe(1.50m);
        balance.TotalValue.Amount.ShouldBe(30m);
    }

    [Fact]
    public void Weighted_average_respects_unequal_quantities()
    {
        // 90 @ £1.00 then 10 @ £2.00 = 100 @ £1.10, not £1.50.
        var balance = Empty();

        balance.ApplyInbound(90m, new Money(1m, "GBP"), Now);
        balance.ApplyInbound(10m, new Money(2m, "GBP"), Now);

        balance.AverageUnitCost.Amount.ShouldBe(1.10m);
    }

    [Fact]
    public void A_sale_does_not_change_average_cost()
    {
        // The property the entire concurrency design rests on (ADR 026).
        var balance = Empty();
        balance.ApplyInbound(10m, new Money(1.50m, "GBP"), Now);

        balance.ApplyOutbound(4m, Now);

        balance.AverageUnitCost.Amount.ShouldBe(1.50m);
        balance.QuantityOnHand.ShouldBe(6m);
        balance.TotalValue.Amount.ShouldBe(9m);
    }

    [Fact]
    public void Stock_may_go_negative()
    {
        // Deliberate (ADR 027). The customer is holding the item; refusing the sale
        // does not fix the data error that caused the zero.
        var balance = Empty();
        balance.ApplyInbound(2m, new Money(1m, "GBP"), Now);

        balance.ApplyOutbound(5m, Now);

        balance.QuantityOnHand.ShouldBe(-3m);
        balance.IsNegative.ShouldBeTrue();
    }

    [Fact]
    public void A_receipt_onto_negative_stock_adopts_the_incoming_cost()
    {
        // On-hand is -5 because a delivery was never booked in. Blending the incoming
        // cost against a meaningless negative value would distort the average, so the
        // incoming cost is adopted wholesale.
        var balance = Empty();
        balance.ApplyInbound(2m, new Money(1m, "GBP"), Now);
        balance.ApplyOutbound(7m, Now);
        balance.QuantityOnHand.ShouldBe(-5m);

        balance.ApplyInbound(10m, new Money(3m, "GBP"), Now);

        balance.QuantityOnHand.ShouldBe(5m);
        balance.AverageUnitCost.Amount.ShouldBe(3m);
    }

    [Fact]
    public void Total_value_stays_consistent_with_quantity_and_average()
    {
        var balance = Empty();
        balance.ApplyInbound(7m, new Money(1.234567m, "GBP"), Now);

        balance.TotalValue.ShouldBe(balance.AverageUnitCost * balance.QuantityOnHand);
    }

    [Fact]
    public void Applying_a_non_positive_quantity_is_rejected()
    {
        var balance = Empty();

        Should.Throw<ArgumentOutOfRangeException>(() => balance.ApplyInbound(0m, new Money(1m, "GBP"), Now));
        Should.Throw<ArgumentOutOfRangeException>(() => balance.ApplyOutbound(-1m, Now));
    }
}

public sealed class WeightedAverageCostingTests
{
    private readonly WeightedAverageCostingPolicy _policy = new();
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Outbound_is_costed_at_the_prevailing_average()
    {
        var balance = StockBalance.Empty(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "GBP");
        balance.ApplyInbound(10m, new Money(2m, "GBP"), Now);

        _policy.CostOutbound(balance, 3m).Amount.ShouldBe(2m);
    }

    [Fact]
    public void Unit_cost_precision_is_not_lost_to_rounding()
    {
        // 3 units at a total of £10 is £3.333…, and rounding that to £3.33 then
        // multiplying back by 3 gives £9.99 — a penny of stock value invented from
        // nothing on every single line. Multiplied across a year, it is a material
        // misstatement of stock value.
        var balance = StockBalance.Empty(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "GBP");

        balance.ApplyInbound(3m, new Money(10m, "GBP") / 3m, Now);

        balance.TotalValue.Amount.ShouldBe(10m, tolerance: 0.0000001m);
    }

    [Fact]
    public void The_policy_agrees_with_the_balance_implementation()
    {
        // Two code paths compute the same average; if they ever disagree, stock
        // valuation depends on which one ran. This test pins them together.
        var balance = StockBalance.Empty(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "GBP");
        balance.ApplyInbound(90m, new Money(1m, "GBP"), Now);

        var viaPolicy = _policy.RecalculateAverage(balance, 10m, new Money(2m, "GBP"));

        balance.ApplyInbound(10m, new Money(2m, "GBP"), Now);

        viaPolicy.ShouldBe(balance.AverageUnitCost);
    }
}

public sealed class LandedCostTests
{
    private static ReceiptLineBasis Line(decimal qty, decimal value) =>
        new(Guid.CreateVersion7(), qty, new Money(value, "GBP"));

    [Fact]
    public void Freight_is_apportioned_by_line_value()
    {
        var freight = new Money(60m, "GBP");
        var lines = new[] { Line(1m, 100m), Line(1m, 200m), Line(1m, 300m) };

        var shares = LandedCostApportionment.Apportion(freight, lines, ApportionmentBasis.Value);

        shares.Select(s => s.Amount).ShouldBe([10m, 20m, 30m]);
    }

    [Fact]
    public void Apportionment_always_sums_to_the_original_cost()
    {
        // A stock valuation that is a penny out is a stock valuation that will not
        // reconcile — and reconciliation is the Phase 4 gate.
        var freight = new Money(100m, "GBP");
        var lines = new[] { Line(1m, 1m), Line(1m, 1m), Line(1m, 1m) };

        var shares = LandedCostApportionment.Apportion(freight, lines, ApportionmentBasis.Value);

        shares.Aggregate(Money.Zero("GBP"), (a, b) => a + b).ShouldBe(freight);
    }

    [Fact]
    public void A_basis_that_cannot_discriminate_falls_back_to_an_even_split()
    {
        // Apportioning by weight when no line carries a weight would otherwise throw
        // from deep inside Allocate with an opaque message.
        var freight = new Money(30m, "GBP");
        var lines = new[] { Line(1m, 10m), Line(1m, 20m), Line(1m, 30m) };

        var shares = LandedCostApportionment.Apportion(freight, lines, ApportionmentBasis.Weight);

        shares.Select(s => s.Amount).ShouldBe([10m, 10m, 10m]);
    }

    [Fact]
    public void Apportionment_by_quantity_uses_units_not_value()
    {
        var freight = new Money(60m, "GBP");
        var lines = new[] { Line(1m, 500m), Line(5m, 100m) };

        var shares = LandedCostApportionment.Apportion(freight, lines, ApportionmentBasis.Quantity);

        shares.Select(s => s.Amount).ShouldBe([10m, 50m]);
    }
}
