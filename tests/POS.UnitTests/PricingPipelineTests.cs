using POS.Sales.Domain;
using POS.Sales.Pricing;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// The invariant these tests protect: the sum of the lines equals the sale total,
/// exactly, on every basket. A one-cent discrepancy here becomes a receipt that
/// disputes itself, a fiscal document a tax authority rejects, and a drawer that
/// will not balance.
/// </summary>
public sealed class PricingPipelineTests
{
    private const string Ccy = "USD";

    private static PricingPipeline BuildPipeline() => new(
    [
        new BasePriceStage(),
        new LineDiscountStage(),
        new PromotionStage(),
        new OrderDiscountStage(),
        new TaxStage(),
        new CashRoundingStage()
    ]);

    private static PricingLineInput Line(
        int number, decimal qty, decimal unitPrice, decimal taxRate = 0.15m,
        bool inclusive = false, Money? manualDiscount = null, Guid? categoryId = null) =>
        new(number, Guid.CreateVersion7(), $"Item {number}", qty,
            new Money(unitPrice, Ccy), "STD", taxRate, inclusive,
            categoryId, manualDiscount);

    private static PricingContext Context(
        IReadOnlyList<PricingLineInput> lines,
        OrderDiscount? orderDiscount = null,
        TaxRoundingRule taxRounding = TaxRoundingRule.PerLine,
        CashRoundingRule? cashRounding = null,
        IReadOnlyList<PromotionDefinition>? promotions = null,
        bool exempt = false) => new()
        {
            Currency = Ccy,
            Lines = lines,
            TaxRounding = taxRounding,
            CashRounding = cashRounding ?? CashRoundingRule.None,
            OrderDiscount = orderDiscount,
            Promotions = promotions ?? [],
            CustomerIsTaxExempt = exempt
        };

    [Fact]
    public void Exclusive_tax_adds_to_the_net_price()
    {
        var result = BuildPipeline().Price(Context([Line(1, 2m, 10.00m)]));

        result.IsSuccess.ShouldBeTrue();
        var line = result.Value.Lines.Single();
        line.Net.Amount.ShouldBe(20.00m);
        line.Tax.Amount.ShouldBe(3.00m);
        line.Gross.Amount.ShouldBe(23.00m);
    }

    [Fact]
    public void Inclusive_tax_is_extracted_from_the_price_not_added_to_it()
    {
        // The classic defect: computing 11.50 × 0.15 = 1.725 instead of
        // 11.50 − (11.50 ÷ 1.15) = 1.50. It overstates tax on every inclusive-priced
        // line, which is most of European and Middle Eastern retail.
        var result = BuildPipeline().Price(
            Context([Line(1, 1m, 11.50m, taxRate: 0.15m, inclusive: true)]));

        var line = result.Value.Lines.Single();
        line.Net.Amount.ShouldBe(10.00m);
        line.Tax.Amount.ShouldBe(1.50m);
        line.Gross.Amount.ShouldBe(11.50m);
    }

    [Fact]
    public void Inclusive_and_exclusive_pricing_agree_when_expressed_equivalently()
    {
        var inclusive = BuildPipeline().Price(
            Context([Line(1, 3m, 11.50m, inclusive: true)])).Value;
        var exclusive = BuildPipeline().Price(
            Context([Line(1, 3m, 10.00m, inclusive: false)])).Value;

        inclusive.TotalInclusiveTax.ShouldBe(exclusive.TotalInclusiveTax);
        inclusive.TotalTax.ShouldBe(exclusive.TotalTax);
    }

    [Fact]
    public void An_order_discount_that_does_not_divide_evenly_still_reconciles()
    {
        // 10.00 across three equal lines is 3.333… each. Naive division yields 3.33
        // three times and loses a cent. The pipeline refuses to produce a basket
        // whose lines do not sum to its total, so this test failing means the
        // allocation is broken.
        var lines = new[] { Line(1, 1m, 10m), Line(2, 1m, 10m), Line(3, 1m, 10m) };
        var discount = new OrderDiscount(OrderDiscountKind.FixedAmount, 10.00m, "Goodwill");

        var result = BuildPipeline().Price(Context(lines, orderDiscount: discount));

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalDiscount.Amount.ShouldBe(10.00m);

        var lineSum = result.Value.Lines.Aggregate(
            Money.Zero(Ccy), (s, l) => s + l.Gross);
        lineSum.ShouldBe(result.Value.TotalInclusiveTax);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(9.99)]
    [InlineData(33.33)]
    [InlineData(100.00)]
    [InlineData(0.07)]
    public void Lines_always_sum_to_the_total_across_awkward_discounts(decimal discountAmount)
    {
        var lines = new[]
        {
            Line(1, 3m, 7.77m), Line(2, 1m, 12.34m),
            Line(3, 2m, 0.99m), Line(4, 7m, 1.05m)
        };

        var result = BuildPipeline().Price(Context(
            lines,
            orderDiscount: new OrderDiscount(OrderDiscountKind.FixedAmount, discountAmount, "Test")));

        result.IsSuccess.ShouldBeTrue();

        result.Value.Lines
              .Aggregate(Money.Zero(Ccy), (s, l) => s + l.Gross)
              .ShouldBe(result.Value.TotalInclusiveTax);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12.5)]
    [InlineData(33.333)]
    public void Percentage_order_discounts_also_reconcile(decimal percentage)
    {
        var lines = new[] { Line(1, 1m, 19.99m), Line(2, 3m, 4.49m), Line(3, 1m, 0.95m) };

        var result = BuildPipeline().Price(Context(
            lines,
            orderDiscount: new OrderDiscount(OrderDiscountKind.Percentage, percentage, "Sale")));

        result.Value.Lines
              .Aggregate(Money.Zero(Ccy), (s, l) => s + l.Gross)
              .ShouldBe(result.Value.TotalInclusiveTax);
    }

    [Fact]
    public void Per_tax_rate_rounding_keeps_the_lines_summing_to_the_tax_total()
    {
        // The alternative rounding rule must preserve the same invariant, or invoice
        // formats that carry both a tax summary and line detail will be rejected.
        var lines = new[]
        {
            Line(1, 1m, 3.33m), Line(2, 1m, 3.33m),
            Line(3, 1m, 3.33m), Line(4, 1m, 6.67m, taxRate: 0.05m)
        };

        var result = BuildPipeline().Price(Context(lines, taxRounding: TaxRoundingRule.PerTaxRate));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Lines
              .Aggregate(Money.Zero(Ccy), (s, l) => s + l.Tax)
              .ShouldBe(result.Value.TotalTax);
    }

    [Fact]
    public void A_tax_exempt_customer_pays_no_tax_but_the_exemption_is_still_traced()
    {
        var result = BuildPipeline().Price(Context([Line(1, 1m, 100m)], exempt: true));

        result.Value.TotalTax.IsZero.ShouldBeTrue();
        result.Value.Lines.Single().Adjustments
              .ShouldContain(a => a.Stage == PricingStage.Tax && a.Description == "Tax exempt");
    }

    [Fact]
    public void Every_stage_leaves_a_trace_so_a_price_can_be_explained()
    {
        // "Why was this 4.37?" must be answerable at the counter without reading code.
        var result = BuildPipeline().Price(Context(
            [Line(1, 2m, 10m, manualDiscount: new Money(2m, Ccy))],
            orderDiscount: new OrderDiscount(OrderDiscountKind.Percentage, 10m, "Loyalty")));

        var stages = result.Value.Lines.Single().Adjustments.Select(a => a.Stage).ToList();

        stages.ShouldContain(PricingStage.BasePrice);
        stages.ShouldContain(PricingStage.LineDiscount);
        stages.ShouldContain(PricingStage.OrderDiscount);
        stages.ShouldContain(PricingStage.Tax);
    }

    [Fact]
    public void Adjustments_are_recorded_in_execution_order()
    {
        var result = BuildPipeline().Price(Context(
            [Line(1, 1m, 50m, manualDiscount: new Money(5m, Ccy))]));

        var sequences = result.Value.Lines.Single().Adjustments.Select(a => a.Sequence).ToList();

        sequences.ShouldBe(sequences.OrderBy(s => s).ToList());
    }

    [Fact]
    public void A_manual_discount_larger_than_the_line_is_rejected()
    {
        var result = BuildPipeline().Price(Context(
            [Line(1, 1m, 10m, manualDiscount: new Money(50m, Ccy))]));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("pricing.discount_exceeds_line");
    }

    [Fact]
    public void Promotions_never_drive_a_line_below_zero()
    {
        // Stacked promotions on a cheap item would otherwise turn a sale line into a
        // refund hidden inside a sale.
        var lines = new[] { Line(1, 1m, 5.00m) };
        var promotions = new[]
        {
            new PromotionDefinition
            {
                Id = Guid.CreateVersion7(), Name = "90% off", Priority = 1,
                Effect = PromotionEffect.PercentageOff, Value = 90m
            },
            new PromotionDefinition
            {
                Id = Guid.CreateVersion7(), Name = "5 off", Priority = 2,
                Effect = PromotionEffect.AmountOffPerUnit, Value = 5m
            }
        };

        var result = BuildPipeline().Price(Context(lines, promotions: promotions));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Lines.Single().Gross.IsNegative.ShouldBeFalse();
        result.Value.TotalInclusiveTax.IsNegative.ShouldBeFalse();
    }

    [Fact]
    public void An_exclusive_promotion_stops_lower_priority_promotions()
    {
        var lines = new[] { Line(1, 1m, 100m) };
        var promotions = new[]
        {
            new PromotionDefinition
            {
                Id = Guid.CreateVersion7(), Name = "First", Priority = 1,
                Effect = PromotionEffect.PercentageOff, Value = 10m, IsExclusive = true
            },
            new PromotionDefinition
            {
                Id = Guid.CreateVersion7(), Name = "Second", Priority = 2,
                Effect = PromotionEffect.PercentageOff, Value = 50m
            }
        };

        var result = BuildPipeline().Price(Context(lines, promotions: promotions));

        result.Value.Lines.Single().Adjustments
              .ShouldNotContain(a => a.Description == "Second");
    }

    [Fact]
    public void Promotions_apply_in_priority_order_regardless_of_declaration_order()
    {
        // Determinism: the same basket and the same promotion set must always produce
        // the same total, whatever order the promotions arrive in.
        var categoryId = Guid.CreateVersion7();
        var lines = new[] { Line(1, 1m, 100m, categoryId: categoryId) };

        var a = new PromotionDefinition
        {
            Id = Guid.CreateVersion7(), Name = "A", Priority = 2,
            Effect = PromotionEffect.PercentageOff, Value = 10m, CategoryId = categoryId
        };
        var b = new PromotionDefinition
        {
            Id = Guid.CreateVersion7(), Name = "B", Priority = 1,
            Effect = PromotionEffect.PercentageOff, Value = 20m, CategoryId = categoryId
        };

        var forward = BuildPipeline().Price(Context(lines, promotions: [a, b])).Value;
        var reversed = BuildPipeline().Price(Context(lines, promotions: [b, a])).Value;

        forward.TotalInclusiveTax.ShouldBe(reversed.TotalInclusiveTax);
    }

    [Fact]
    public void Cash_rounding_adjusts_the_payable_total_without_touching_tax()
    {
        // Swiss-style 0.05 rounding. The invoice total and tax must stay exact; only
        // the cash payable moves, and the difference is reported separately.
        var lines = new[] { Line(1, 1m, 10.02m, taxRate: 0m) };

        var result = BuildPipeline().Price(Context(
            lines, cashRounding: new CashRoundingRule(0.05m)));

        result.IsSuccess.ShouldBeTrue();
        result.Value.RoundingAdjustment.IsZero.ShouldBeFalse();
        (result.Value.TotalExclusiveTax + result.Value.TotalTax + result.Value.RoundingAdjustment)
            .ShouldBe(result.Value.TotalInclusiveTax);
    }

    [Fact]
    public void Pricing_is_deterministic_across_repeated_runs()
    {
        var lines = new[] { Line(1, 3m, 7.77m), Line(2, 2m, 1.11m) };
        var context = Context(
            lines, orderDiscount: new OrderDiscount(OrderDiscountKind.Percentage, 7m, "X"));

        var first = BuildPipeline().Price(context).Value;
        var second = BuildPipeline().Price(context).Value;

        first.TotalInclusiveTax.ShouldBe(second.TotalInclusiveTax);
        first.TotalTax.ShouldBe(second.TotalTax);
        first.TotalDiscount.ShouldBe(second.TotalDiscount);
    }
}
