using POS.Sales.Domain;
using POS.SharedKernel;

namespace POS.Sales.Pricing;

/// <summary>
/// A deterministic, ordered pipeline that turns a basket into final line amounts.
/// </summary>
/// <remarks>
/// <para>
/// Ordered stages rather than scattered conditionals, because pricing is where retail
/// systems rot. "Buy 2 get 1 free on category X, except Tuesdays, unless the customer
/// is in group Y" arrives eventually, and once it is expressed as nested <c>if</c>
/// statements nobody can predict the output or change it safely.
/// </para>
/// <para>
/// Two properties are enforced structurally:
/// </para>
/// <list type="number">
///   <item><b>Determinism.</b> Same basket, same configuration, same instant produces
///   the same total, always. Stages run in a fixed order and none may read the clock
///   or query live data — everything is snapshotted into the context first.</item>
///   <item><b>Explainability.</b> Every stage appends a <see cref="PriceAdjustment"/>
///   to the line. A manager facing a disputed price sees the ordered trace rather
///   than being told the computer says 4.37. See ADR 034.</item>
/// </list>
/// <para>
/// The stage list is fixed and closed. Extension happens by adding data — promotion
/// definitions — not by adding stages, and emphatically not by embedding a scripting
/// language. See ADR 035.
/// </para>
/// </remarks>
public sealed class PricingPipeline(IReadOnlyList<IPricingStage> stages)
{
    /// <summary>The canonical order. Changing it changes what customers are charged.</summary>
    /// <remarks>
    /// Order matters and is not arbitrary. Line discounts apply before promotions so a
    /// manual markdown does not stack unexpectedly on top of a promotional price;
    /// order-level discounts apply after line-level so the percentage is taken on the
    /// already-discounted subtotal; tax is second-to-last because in most jurisdictions
    /// it applies to the net actually charged; rounding is last because it is a
    /// property of the payable total, not of any line.
    /// </remarks>
    public static IReadOnlyList<PricingStage> CanonicalOrder =>
    [
        Domain.PricingStage.BasePrice,
        Domain.PricingStage.LineDiscount,
        Domain.PricingStage.Promotion,
        Domain.PricingStage.OrderDiscount,
        Domain.PricingStage.Coupon,
        Domain.PricingStage.Tax,
        Domain.PricingStage.Rounding
    ];

    private readonly IReadOnlyList<IPricingStage> _stages =
        stages.OrderBy(s => CanonicalOrder.ToList().IndexOf(s.Stage)).ToList();

    public Result<PricingOutcome> Price(PricingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = PricingState.From(context);

        foreach (var stage in _stages)
        {
            var result = stage.Apply(state, context);
            if (result.IsFailure)
            {
                return Result<PricingOutcome>.Failure(result.Error);
            }
        }

        var outcome = state.ToOutcome();

        // The invariant the whole module exists to protect. If the lines do not sum
        // to the total, the receipt is wrong, the fiscal document is wrong, and the
        // drawer will not balance — so it is asserted here rather than hoped for.
        var lineSum = outcome.Lines.Aggregate(
            Money.Zero(context.Currency), (sum, l) => sum + l.Gross);

        if (lineSum + outcome.RoundingAdjustment != outcome.TotalInclusiveTax)
        {
            return Result<PricingOutcome>.Failure(PricingErrors.TotalsDoNotReconcile(
                lineSum, outcome.RoundingAdjustment, outcome.TotalInclusiveTax));
        }

        return Result<PricingOutcome>.Success(outcome);
    }
}

public interface IPricingStage
{
    public PricingStage Stage { get; }
    public Result Apply(PricingState state, PricingContext context);
}

/// <summary>Immutable inputs, snapshotted before pricing begins.</summary>
/// <remarks>
/// Snapshotting is what makes the pipeline deterministic and testable. A stage that
/// could query a price list mid-run would produce different totals depending on when
/// it executed relative to a catalog edit.
/// </remarks>
public sealed record PricingContext
{
    public required string Currency { get; init; }
    public required IReadOnlyList<PricingLineInput> Lines { get; init; }
    public required TaxRoundingRule TaxRounding { get; init; }
    public required CashRoundingRule CashRounding { get; init; }

    public IReadOnlyList<PromotionDefinition> Promotions { get; init; } = [];
    public OrderDiscount? OrderDiscount { get; init; }
    public Guid? CustomerGroupId { get; init; }
    public bool CustomerIsTaxExempt { get; init; }
}

public sealed record PricingLineInput(
    int LineNumber,
    Guid VariantId,
    string Description,
    decimal Quantity,
    Money UnitPrice,
    string TaxCode,
    decimal TaxRate,
    bool TaxInclusivePricing,
    Guid? CategoryId = null,
    Money? ManualDiscount = null,
    Guid? ManualDiscountAuthorisedBy = null);

/// <summary>
/// Where tax rounding happens.
/// </summary>
/// <remarks>
/// Not a technical detail — jurisdictions genuinely differ, and the two rules produce
/// different totals on the same basket. Modelled as configuration on the company
/// rather than detected from a country code, consistent with the fiscal design in
/// ADR 031: behaviour is chosen by data, never by inspecting where the customer is.
/// </remarks>
public enum TaxRoundingRule
{
    /// <summary>Round tax on each line, then sum. Most common; matches most fiscal formats.</summary>
    PerLine = 0,

    /// <summary>Sum unrounded line tax, round once per tax rate at invoice level.</summary>
    PerTaxRate = 1
}

/// <summary>Cash rounding for currencies without small denominations.</summary>
/// <remarks>
/// Swiss 0.05 and Swedish 1.00 rounding apply to the CASH payable total only; the
/// invoice total and the tax remain unrounded, and the difference is a separate
/// reported line. Applying it to the invoice total instead would misstate tax.
/// </remarks>
public sealed record CashRoundingRule(decimal Increment)
{
    public static CashRoundingRule None => new(0m);
    public bool IsEnabled => Increment > 0m;
}

public sealed record OrderDiscount(
    OrderDiscountKind Kind,
    decimal Value,
    string Description,
    Guid? AuthorisedBy = null);

public enum OrderDiscountKind { Percentage = 0, FixedAmount = 1 }

/// <summary>Mutable working state threaded through the stages.</summary>
public sealed class PricingState
{
    private PricingState(string currency, List<PricingLineState> lines)
    {
        Currency = currency;
        Lines = lines;
    }

    public string Currency { get; }
    public List<PricingLineState> Lines { get; }
    public Money RoundingAdjustment { get; set; }
    private int _sequence;

    public int NextSequence() => ++_sequence;

    public static PricingState From(PricingContext context)
    {
        var lines = context.Lines.Select(l => new PricingLineState(l, context.Currency)).ToList();
        return new PricingState(context.Currency, lines)
        {
            RoundingAdjustment = Money.Zero(context.Currency)
        };
    }

    public PricingOutcome ToOutcome()
    {
        var zero = Money.Zero(Currency);
        var lines = Lines.Select(l => l.ToOutcome()).ToList();

        return new PricingOutcome
        {
            Lines = lines,
            TotalDiscount = lines.Aggregate(zero, (s, l) => s + l.Discount),
            TotalExclusiveTax = lines.Aggregate(zero, (s, l) => s + l.Net),
            TotalTax = lines.Aggregate(zero, (s, l) => s + l.Tax),
            RoundingAdjustment = RoundingAdjustment,
            TotalInclusiveTax = lines.Aggregate(zero, (s, l) => s + l.Gross) + RoundingAdjustment
        };
    }
}

public sealed class PricingLineState(PricingLineInput input, string currency)
{
    public PricingLineInput Input { get; } = input;
    public List<PriceAdjustment> Adjustments { get; } = [];

    /// <summary>Extended price before any discount.</summary>
    public Money Extended { get; set; } = input.UnitPrice * input.Quantity;

    public Money Discount { get; set; } = Money.Zero(currency);
    public Money Net { get; set; } = Money.Zero(currency);
    public Money Tax { get; set; } = Money.Zero(currency);
    public Money Gross { get; set; } = Money.Zero(currency);

    /// <summary>Unrounded tax, retained so PerTaxRate rounding can work from exact figures.</summary>
    public decimal UnroundedTax { get; set; }

    public PricingLineOutcome ToOutcome() => new(
        Input.LineNumber, Input.VariantId, Discount, Net, Tax, Gross, Adjustments);
}

public sealed record PricingOutcome
{
    public required IReadOnlyList<PricingLineOutcome> Lines { get; init; }
    public required Money TotalDiscount { get; init; }
    public required Money TotalExclusiveTax { get; init; }
    public required Money TotalTax { get; init; }
    public required Money RoundingAdjustment { get; init; }
    public required Money TotalInclusiveTax { get; init; }
}

public sealed record PricingLineOutcome(
    int LineNumber,
    Guid VariantId,
    Money Discount,
    Money Net,
    Money Tax,
    Money Gross,
    IReadOnlyList<PriceAdjustment> Adjustments);

public static class PricingErrors
{
    public static Error TotalsDoNotReconcile(Money lineSum, Money rounding, Money total) =>
        Error.BusinessRule(
            "pricing.totals_do_not_reconcile",
            $"Line total {lineSum} plus rounding {rounding} does not equal the sale " +
            $"total {total}. Refusing to complete a sale whose arithmetic is inconsistent.");

    public static Error DiscountExceedsLine => Error.BusinessRule(
        "pricing.discount_exceeds_line", "A discount cannot exceed the line value.");

    public static Error NegativeTotal => Error.BusinessRule(
        "pricing.negative_total", "Pricing produced a negative total.");
}
