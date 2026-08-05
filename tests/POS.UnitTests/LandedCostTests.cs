using POS.Purchasing.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// The allocator is a pure function, so it is tested as one. These are the tests that
/// matter most in Phase 7: everything downstream — weighted average cost, margin,
/// stock valuation — is built on the numbers this class produces.
/// </summary>
public sealed class LandedCostAllocationTests
{
    private static readonly Guid VariantA = Guid.CreateVersion7();
    private static readonly Guid VariantB = Guid.CreateVersion7();
    private static readonly Guid VariantC = Guid.CreateVersion7();

    private static Money M(decimal amount) => new(amount, PurchasingFixtures.Gbp);

    private static GoodsReceiptLine Line(int number, Guid variantId, decimal quantity, decimal unitPrice) =>
        new(number, variantId, quantity, M(unitPrice));

    [Fact]
    public void Freight_is_allocated_by_quantity_because_a_lorry_carries_units_not_value()
    {
        // 30 units in total, split 10/20. Freight of 30 should follow 1:2.
        var lines = new[]
        {
            Line(1, VariantA, 10m, 5m),
            Line(2, VariantB, 20m, 100m)
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Freight, M(30m), "INV-FREIGHT-1", LandedCostAllocationBasis.Quantity)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated[0].ShouldBe(M(10m));
        allocated[1].ShouldBe(M(20m));
    }

    [Fact]
    public void Duty_is_allocated_by_value_because_customs_charges_on_value_not_bulk()
    {
        // Line values: 10 x 5 = 50, 20 x 100 = 2000. Total 2050.
        // Duty of 205 should follow value, not the quantity split above.
        var lines = new[]
        {
            Line(1, VariantA, 10m, 5m),
            Line(2, VariantB, 20m, 100m)
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Duty, M(205m), "C88-1", LandedCostAllocationBasis.Value)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated[0].ShouldBe(M(5m));
        allocated[1].ShouldBe(M(200m));
    }

    [Fact]
    public void An_even_basis_ignores_both_quantity_and_value()
    {
        var lines = new[]
        {
            Line(1, VariantA, 1m, 1m),
            Line(2, VariantB, 999m, 999m)
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Handling, M(10m), "HANDLING", LandedCostAllocationBasis.Even)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated[0].ShouldBe(M(5m));
        allocated[1].ShouldBe(M(5m));
    }

    [Fact]
    public void An_indivisible_charge_is_allocated_to_the_penny_and_never_loses_one()
    {
        // 10.00 across three equal lines does not divide. The allocator must still sum
        // exactly to the charge, or stock valuation stops reconciling to the purchase
        // ledger by a cent per delivery.
        var lines = new[]
        {
            Line(1, VariantA, 1m, 1m),
            Line(2, VariantB, 1m, 1m),
            Line(3, VariantC, 1m, 1m)
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Freight, M(10m), "F", LandedCostAllocationBasis.Quantity)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated.Aggregate(Money.Zero(PurchasingFixtures.Gbp), (sum, share) => sum + share)
            .ShouldBe(M(10m));

        // Largest-remainder: the extra penny lands on the earliest line, deterministically.
        allocated[0].ShouldBe(M(3.34m));
        allocated[1].ShouldBe(M(3.33m));
        allocated[2].ShouldBe(M(3.33m));
    }

    [Fact]
    public void Several_charges_on_different_bases_accumulate_per_line()
    {
        var lines = new[]
        {
            Line(1, VariantA, 10m, 5m),   // value 50
            Line(2, VariantB, 20m, 100m)  // value 2000
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Freight, M(30m), "F", LandedCostAllocationBasis.Quantity),
            new LandedCostCharge(LandedCostType.Duty, M(205m), "D", LandedCostAllocationBasis.Value)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated[0].ShouldBe(M(15m));   // 10 freight + 5 duty
        allocated[1].ShouldBe(M(220m));  // 20 freight + 200 duty

        allocated.Aggregate(Money.Zero(PurchasingFixtures.Gbp), (sum, share) => sum + share)
            .ShouldBe(M(235m));
    }

    [Fact]
    public void A_basis_that_cannot_discriminate_falls_back_to_an_even_split_rather_than_dropping_the_charge()
    {
        // Free samples: every line has zero value, so a value basis has nothing to weigh.
        // The freight was still paid, and it must land somewhere.
        var lines = new[]
        {
            Line(1, VariantA, 5m, 0m),
            Line(2, VariantB, 5m, 0m)
        };

        var charges = new[]
        {
            new LandedCostCharge(LandedCostType.Freight, M(9m), "F", LandedCostAllocationBasis.Value)
        };

        var allocated = LandedCostAllocator.Allocate(lines, charges, PurchasingFixtures.Gbp);

        allocated[0].ShouldBe(M(4.50m));
        allocated[1].ShouldBe(M(4.50m));
    }

    [Fact]
    public void A_delivery_with_no_landed_costs_allocates_zero_to_every_line()
    {
        var lines = new[]
        {
            Line(1, VariantA, 10m, 5m),
            Line(2, VariantB, 20m, 100m)
        };

        var allocated = LandedCostAllocator.Allocate(lines, [], PurchasingFixtures.Gbp);

        allocated.Count.ShouldBe(2);
        allocated.ShouldAllBe(share => share.IsZero);
    }
}

/// <summary>
/// The late-landed-cost problem: the freight invoice arrives three weeks after the goods,
/// by which time some of them have been sold. See ADR 049.
/// </summary>
public sealed class LateLandedCostTests
{
    private static Money M(decimal amount) => new(amount, PurchasingFixtures.Gbp);

    [Fact]
    public void If_everything_received_is_still_on_hand_the_whole_charge_revalues_stock()
    {
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 50m, quantityStillOnHand: 50m);

        split.Revaluation.ShouldBe(M(100m));
        split.Variance.IsZero.ShouldBeTrue();
        split.ProportionOnHand.ShouldBe(1m);
    }

    [Fact]
    public void If_everything_has_been_sold_the_whole_charge_is_a_variance_because_there_is_nothing_left_to_revalue()
    {
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 50m, quantityStillOnHand: 0m);

        split.Revaluation.IsZero.ShouldBeTrue();
        split.Variance.ShouldBe(M(100m));
        split.ProportionOnHand.ShouldBe(0m);
    }

    [Fact]
    public void A_partly_sold_delivery_splits_the_charge_by_units_remaining()
    {
        // 30 of 50 remain: 60% revalues stock, 40% is a cost of goods already sold.
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 50m, quantityStillOnHand: 30m);

        split.Revaluation.ShouldBe(M(60m));
        split.Variance.ShouldBe(M(40m));
        split.ProportionOnHand.ShouldBe(0.6m);
    }

    [Fact]
    public void The_two_halves_always_sum_to_the_original_charge_even_when_the_split_does_not_divide()
    {
        // 1/3 on hand of a charge of 10.00 — no exact split exists.
        var split = LateLandedCostAllocator.Split(M(10m), quantityReceived: 3m, quantityStillOnHand: 1m);

        (split.Revaluation + split.Variance).ShouldBe(M(10m));
    }

    [Fact]
    public void More_on_hand_than_was_received_is_capped_because_later_deliveries_did_not_incur_this_charge()
    {
        // 80 on hand, but this receipt only brought 50. The other 30 came from a second
        // delivery with its own freight; charging them again would double-count.
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 50m, quantityStillOnHand: 80m);

        split.Revaluation.ShouldBe(M(100m));
        split.Variance.IsZero.ShouldBeTrue();
        split.ProportionOnHand.ShouldBe(1m);
    }

    [Fact]
    public void Negative_stock_is_treated_as_nothing_on_hand_so_the_charge_surfaces_as_variance()
    {
        // ADR 027 permits negative stock. It cannot be revalued — there are no units
        // present to carry the cost — so the charge is expensed where someone will see it.
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 50m, quantityStillOnHand: -5m);

        split.Revaluation.IsZero.ShouldBeTrue();
        split.Variance.ShouldBe(M(100m));
    }

    [Fact]
    public void A_charge_against_a_receipt_that_brought_in_nothing_is_a_programming_error_not_a_business_case()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            LateLandedCostAllocator.Split(M(100m), quantityReceived: 0m, quantityStillOnHand: 0m));
    }
}
