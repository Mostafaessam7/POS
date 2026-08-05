using POS.Sales.Domain;
using POS.SharedKernel;

namespace POS.Sales.Pricing;

/// <summary>Stage 1 — establishes the extended price and records it in the trace.</summary>
public sealed class BasePriceStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.BasePrice;

    public Result Apply(PricingState state, PricingContext context)
    {
        foreach (var line in state.Lines)
        {
            line.Extended = line.Input.UnitPrice * line.Input.Quantity;

            line.Adjustments.Add(new PriceAdjustment(
                state.NextSequence(),
                Domain.PricingStage.BasePrice,
                $"{line.Input.Quantity} × {line.Input.UnitPrice}",
                line.Extended));
        }

        return Result.Success();
    }
}

/// <summary>Stage 2 — manual, permission-gated markdowns.</summary>
/// <remarks>
/// The authorising principal is recorded on the adjustment, not just the amount.
/// Discount abuse is a common shrinkage vector, and "who approved this" is the first
/// question asked; discount frequency per operator should be a standing report.
/// </remarks>
public sealed class LineDiscountStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.LineDiscount;

    public Result Apply(PricingState state, PricingContext context)
    {
        foreach (var line in state.Lines)
        {
            if (line.Input.ManualDiscount is not { } discount || discount.IsZero)
            {
                continue;
            }

            if (discount > line.Extended)
            {
                return Result.Failure(PricingErrors.DiscountExceedsLine);
            }

            line.Discount += discount;

            line.Adjustments.Add(new PriceAdjustment(
                state.NextSequence(),
                Domain.PricingStage.LineDiscount,
                "Manual discount",
                -discount,
                AuthorisedBy: line.Input.ManualDiscountAuthorisedBy));
        }

        return Result.Success();
    }
}

/// <summary>Stage 3 — promotions, evaluated in priority order.</summary>
/// <remarks>
/// Promotions are DATA with a closed set of condition and effect types (ADR 035).
/// This stage interprets that data; it contains no promotion-specific logic and grows
/// only when a genuinely new effect type is needed, which should be rare and
/// deliberate.
/// </remarks>
public sealed class PromotionStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.Promotion;

    public Result Apply(PricingState state, PricingContext context)
    {
        foreach (var promotion in context.Promotions.OrderBy(p => p.Priority))
        {
            var eligible = state.Lines.Where(l => promotion.Matches(l.Input)).ToList();
            if (eligible.Count == 0)
            {
                continue;
            }

            foreach (var line in eligible)
            {
                var remaining = line.Extended - line.Discount;

                var benefit = promotion.Effect switch
                {
                    PromotionEffect.PercentageOff =>
                        (remaining * (promotion.Value / 100m)).RoundToCurrency(),
                    PromotionEffect.AmountOffPerUnit =>
                        new Money(promotion.Value, state.Currency) * line.Input.Quantity,
                    PromotionEffect.FixedUnitPrice =>
                        remaining - (new Money(promotion.Value, state.Currency) * line.Input.Quantity),
                    _ => Money.Zero(state.Currency)
                };

                if (benefit.IsNegative || benefit.IsZero)
                {
                    continue;
                }

                // Never let stacked promotions drive a line below zero. A negative
                // line would silently become a refund inside a sale.
                if (benefit > remaining)
                {
                    benefit = remaining;
                }

                line.Discount += benefit;

                line.Adjustments.Add(new PriceAdjustment(
                    state.NextSequence(),
                    Domain.PricingStage.Promotion,
                    promotion.Name,
                    -benefit,
                    SourceId: promotion.Id));
            }

            if (promotion.IsExclusive)
            {
                break;
            }
        }

        return Result.Success();
    }
}

/// <summary>Stage 4 — an order-level discount spread across the lines.</summary>
/// <remarks>
/// <para>
/// This is where naive implementations lose money. A 10% order discount on three
/// lines cannot simply be divided: the shares round, and the rounded shares do not
/// sum back to the discount. The residual cent then appears as a total that does not
/// match the sum of its lines.
/// </para>
/// <para>
/// <c>Money.Allocate</c> distributes by largest remainder so the shares sum EXACTLY
/// to the discount. The discount must also be pushed down to the lines rather than
/// held at order level, because tax is computed per line and a discount invisible to
/// the lines would produce tax on an amount the customer never paid.
/// </para>
/// </remarks>
public sealed class OrderDiscountStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.OrderDiscount;

    public Result Apply(PricingState state, PricingContext context)
    {
        if (context.OrderDiscount is not { } discount)
        {
            return Result.Success();
        }

        var zero = Money.Zero(state.Currency);
        var subtotal = state.Lines.Aggregate(zero, (sum, l) => sum + (l.Extended - l.Discount));

        if (subtotal.IsZero || subtotal.IsNegative)
        {
            return Result.Success();
        }

        var totalDiscount = discount.Kind switch
        {
            OrderDiscountKind.Percentage => (subtotal * (discount.Value / 100m)).RoundToCurrency(),
            OrderDiscountKind.FixedAmount => new Money(discount.Value, state.Currency),
            _ => zero
        };

        if (totalDiscount > subtotal)
        {
            totalDiscount = subtotal;
        }

        // Weight by each line's remaining value so an expensive line absorbs
        // proportionally more of the discount.
        var weights = state.Lines
            .Select(l => (l.Extended - l.Discount).Amount)
            .ToList();

        if (weights.Sum() == 0m)
        {
            return Result.Success();
        }

        var shares = totalDiscount.Allocate(weights);

        for (var i = 0; i < state.Lines.Count; i++)
        {
            if (shares[i].IsZero)
            {
                continue;
            }

            state.Lines[i].Discount += shares[i];

            state.Lines[i].Adjustments.Add(new PriceAdjustment(
                state.NextSequence(),
                Domain.PricingStage.OrderDiscount,
                discount.Description,
                -shares[i],
                AuthorisedBy: discount.AuthorisedBy));
        }

        return Result.Success();
    }
}

/// <summary>
/// Stage 6 — tax, handling inclusive and exclusive pricing.
/// </summary>
/// <remarks>
/// <para>
/// Inclusive pricing is not a display concern. When a shelf price of 11.50 includes
/// 15% tax, the net is 11.50 ÷ 1.15 and the tax is the remainder — computing tax as
/// 11.50 × 0.15 overstates it and is one of the most common defects in retail
/// software. Both directions are implemented here and tested against each other.
/// </para>
/// <para>
/// Tax-exempt customers zero the tax rather than skipping the stage, so the exemption
/// still appears in the adjustment trace and on the fiscal document.
/// </para>
/// </remarks>
public sealed class TaxStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.Tax;

    public Result Apply(PricingState state, PricingContext context)
    {
        foreach (var line in state.Lines)
        {
            var payable = line.Extended - line.Discount;
            var rate = context.CustomerIsTaxExempt ? 0m : line.Input.TaxRate;

            decimal netAmount;
            decimal taxAmount;

            if (line.Input.TaxInclusivePricing)
            {
                netAmount = payable.Amount / (1m + rate);
                taxAmount = payable.Amount - netAmount;
            }
            else
            {
                netAmount = payable.Amount;
                taxAmount = netAmount * rate;
            }

            line.UnroundedTax = taxAmount;

            var net = new Money(netAmount, state.Currency).RoundToCurrency();
            var tax = context.TaxRounding == TaxRoundingRule.PerLine
                ? new Money(taxAmount, state.Currency).RoundToCurrency()
                : new Money(taxAmount, state.Currency);

            line.Net = net;
            line.Tax = tax;

            // Derive gross from the rounded components rather than rounding the
            // original payable independently. Rounding two figures separately and
            // hoping they agree is exactly how a one-cent discrepancy appears
            // between the receipt total and the sum of its lines.
            line.Gross = net + tax;

            line.Adjustments.Add(new PriceAdjustment(
                state.NextSequence(),
                Domain.PricingStage.Tax,
                context.CustomerIsTaxExempt
                    ? "Tax exempt"
                    : $"{line.Input.TaxCode} @ {rate:P2}"
                      + (line.Input.TaxInclusivePricing ? " (inclusive)" : ""),
                tax));
        }

        if (context.TaxRounding == TaxRoundingRule.PerTaxRate)
        {
            ApplyPerTaxRateRounding(state);
        }

        return Result.Success();
    }

    /// <summary>
    /// Rounds once per tax rate and redistributes so the lines still sum to the
    /// rounded total.
    /// </summary>
    /// <remarks>
    /// Without redistribution the invoice-level tax total and the sum of the line tax
    /// figures disagree, which most statutory invoice formats reject outright.
    /// </remarks>
    private static void ApplyPerTaxRateRounding(PricingState state)
    {
        foreach (var group in state.Lines.GroupBy(l => l.Input.TaxRate))
        {
            var lines = group.ToList();
            var exactTotal = lines.Sum(l => l.UnroundedTax);
            var roundedTotal = new Money(exactTotal, state.Currency).RoundToCurrency();

            var weights = lines.Select(l => l.UnroundedTax).ToList();

            var shares = weights.Sum() == 0m
                ? lines.Select(_ => Money.Zero(state.Currency)).ToArray()
                : roundedTotal.Allocate(weights);

            for (var i = 0; i < lines.Count; i++)
            {
                lines[i].Tax = shares[i];
                lines[i].Gross = lines[i].Net + shares[i];
            }
        }
    }
}

/// <summary>Stage 7 — cash rounding of the payable total.</summary>
/// <remarks>
/// Applied to the total, never to a line, and recorded as its own figure. Where a
/// currency has no 0.01 coin the payable amount is rounded to the nearest available
/// increment while the invoice and tax totals stay exact — the difference is a
/// separate, reportable adjustment rather than a silent alteration of the sale.
/// </remarks>
public sealed class CashRoundingStage : IPricingStage
{
    public PricingStage Stage => Domain.PricingStage.Rounding;

    public Result Apply(PricingState state, PricingContext context)
    {
        if (!context.CashRounding.IsEnabled)
        {
            return Result.Success();
        }

        var zero = Money.Zero(state.Currency);
        var total = state.Lines.Aggregate(zero, (sum, l) => sum + l.Gross);

        var increment = context.CashRounding.Increment;
        var rounded = Math.Round(total.Amount / increment, 0, MidpointRounding.AwayFromZero) * increment;

        state.RoundingAdjustment = new Money(rounded - total.Amount, state.Currency);

        return Result.Success();
    }
}

/// <summary>A promotion expressed as data, not code.</summary>
/// <remarks>
/// A closed set of conditions and effects (ADR 035). The temptation is always to add
/// a rules language so marketing can write arbitrary logic; the result is an
/// untestable, unauditable interpreter in the most correctness-critical path of the
/// product. When a promotion cannot be expressed here, the honest answer is to add
/// one new effect type deliberately rather than to make everything expressible.
/// </remarks>
public sealed record PromotionDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Priority { get; init; }
    public required PromotionEffect Effect { get; init; }
    public required decimal Value { get; init; }

    public Guid? VariantId { get; init; }
    public Guid? CategoryId { get; init; }
    public decimal? MinimumQuantity { get; init; }

    /// <summary>When true, no lower-priority promotion is evaluated after this one.</summary>
    public bool IsExclusive { get; init; }

    public bool Matches(PricingLineInput line)
    {
        if (VariantId is { } variantId && line.VariantId != variantId)
        {
            return false;
        }

        if (CategoryId is { } categoryId && line.CategoryId != categoryId)
        {
            return false;
        }

        return MinimumQuantity is not { } minimum || line.Quantity >= minimum;
    }
}

public enum PromotionEffect
{
    PercentageOff = 0,
    AmountOffPerUnit = 1,
    FixedUnitPrice = 2
}
