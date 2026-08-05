using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class MoneyAllocationTests
{
    private static Money Gbp(decimal amount) => new(amount, "GBP");

    [Fact]
    public void Allocation_of_an_indivisible_amount_still_sums_exactly()
    {
        // The classic failure: 100.00 / 3 = 33.33 each, summing to 99.99.
        // One penny vanishes and the sale no longer balances.
        var shares = Gbp(100m).Allocate(3);

        shares.Select(s => s.Amount).ShouldBe([33.34m, 33.33m, 33.33m]);
        shares.Aggregate(Gbp(0m), (a, b) => a + b).ShouldBe(Gbp(100m));
    }

    [Theory]
    [InlineData(0.01, 3)]
    [InlineData(0.02, 3)]
    [InlineData(10, 7)]
    [InlineData(999.99, 11)]
    [InlineData(0.05, 100)]
    public void Every_allocation_sums_back_to_the_original(decimal amount, int parts)
    {
        var original = Gbp(amount);

        original.Allocate(parts)
                .Aggregate(Gbp(0m), (a, b) => a + b)
                .ShouldBe(original);
    }

    [Fact]
    public void Weighted_allocation_respects_the_weights()
    {
        // Apportioning 60.00 of freight across lines worth 100, 200 and 300.
        var shares = Gbp(60m).Allocate([100m, 200m, 300m]);

        shares.Select(s => s.Amount).ShouldBe([10m, 20m, 30m]);
    }

    [Fact]
    public void Weighted_allocation_with_a_remainder_still_sums_exactly()
    {
        var original = Gbp(10m);

        var shares = original.Allocate([1m, 1m, 1m]);

        shares.Aggregate(Gbp(0m), (a, b) => a + b).ShouldBe(original);
    }

    [Fact]
    public void A_zero_weight_receives_nothing()
    {
        // A line that contributed no value must not absorb a stray penny of freight.
        var shares = Gbp(10m).Allocate([1m, 0m, 1m]);

        shares[1].IsZero.ShouldBeTrue();
        shares.Aggregate(Gbp(0m), (a, b) => a + b).ShouldBe(Gbp(10m));
    }

    [Fact]
    public void A_negative_amount_allocates_in_the_right_direction()
    {
        // Refunds and credit notes allocate too; the remainder must not flip sign.
        var shares = Gbp(-100m).Allocate(3);

        shares.ShouldAllBe(s => s.Amount < 0m);
        shares.Aggregate(Gbp(0m), (a, b) => a + b).ShouldBe(Gbp(-100m));
    }

    [Fact]
    public void Allocation_respects_currencies_with_no_minor_unit()
    {
        // Yen has no subunit. Allocating 100 across 3 must produce whole yen,
        // never 33.33, which no payment terminal can settle.
        var shares = new Money(100m, "JPY").Allocate(3);

        shares.Select(s => s.Amount).ShouldBe([34m, 33m, 33m]);
    }

    [Fact]
    public void Weights_that_are_all_zero_are_rejected()
    {
        Should.Throw<ArgumentException>(() => Gbp(10m).Allocate([0m, 0m]));
    }
}

public sealed class MoneyDivisionTests
{
    [Fact]
    public void Division_retains_full_precision_for_unit_costs()
    {
        // A weighted average unit cost rounded to 2dp and multiplied back up
        // reintroduces exactly the drift Money exists to prevent (ADR 020).
        var unitCost = new Money(100m, "GBP") / 3m;

        unitCost.Amount.ShouldBe(33.3333333333333333333333333333m, tolerance: 0.0000000001m);
    }

    [Fact]
    public void Dividing_by_zero_throws()
    {
        Should.Throw<DivideByZeroException>(() => new Money(10m, "GBP") / 0m);
    }

    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("GBP", 2)]
    [InlineData("USD", 2)]
    public void Currency_decimal_places_are_known(string currency, int expected)
    {
        new Money(1m, currency).DecimalPlaces.ShouldBe(expected);
    }

    [Fact]
    public void Rounding_to_currency_uses_the_currency_precision()
    {
        new Money(1234.567m, "JPY").RoundToCurrency().Amount.ShouldBe(1235m);
        new Money(1234.567m, "GBP").RoundToCurrency().Amount.ShouldBe(1234.57m);
    }
}

public sealed class MoneyUninitialisedTests
{
    [Fact]
    public void Default_Money_is_detected_as_uninitialised()
    {
        default(Money).IsUninitialised.ShouldBeTrue();
        new Money(0m, "GBP").IsUninitialised.ShouldBeFalse();
    }

    [Fact]
    public void Operating_on_default_Money_names_the_real_problem()
    {
        // Without this, the failure surfaces as "Currency mismatch:  and GBP",
        // which sends the reader looking for a currency bug that does not exist.
        var ex = Should.Throw<InvalidOperationException>(
            () => default(Money) + new Money(1m, "GBP"));

        ex.Message.ShouldContain("uninitialised");
    }
}
