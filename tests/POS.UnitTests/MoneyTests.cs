using POS.SharedKernel;
using Shouldly;

namespace POS.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void Adding_different_currencies_throws()
    {
        var gbp = new Money(10m, "GBP");
        var usd = new Money(10m, "USD");

        // A currency mismatch is always a bug, never a runtime condition to
        // tolerate by silently coercing.
        Should.Throw<InvalidOperationException>(() => gbp + usd);
    }

    [Theory]
    [InlineData(2.345, 2.35)]   // commercial rounding: 0.5 away from zero
    [InlineData(2.355, 2.36)]
    [InlineData(-2.345, -2.35)]
    [InlineData(2.344, 2.34)]
    public void Rounds_half_away_from_zero(decimal input, decimal expected)
    {
        // Deliberately NOT banker's rounding, which is the .NET default and would
        // give 2.34 for the first case. This is a policy decision recorded in
        // ADR 024, and Money.Round is the only rounding entry point in the system.
        new Money(input, "GBP").Round().Amount.ShouldBe(expected);
    }

    [Fact]
    public void Repeated_addition_does_not_drift()
    {
        // The reason money is decimal and never double. With double, summing 0.10
        // one hundred times gives 9.999999999999831 — a drawer that will not
        // balance, discovered at close of trade.
        var total = Money.Zero("GBP");

        for (var i = 0; i < 100; i++)
            total += new Money(0.10m, "GBP");

        total.Amount.ShouldBe(10.00m);
    }

    [Fact]
    public void Rejects_non_iso_currency_codes()
    {
        Should.Throw<ArgumentException>(() => new Money(1m, "POUNDS"));
        Should.Throw<ArgumentException>(() => new Money(1m, ""));
    }
}
