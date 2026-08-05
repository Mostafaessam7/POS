using POS.SharedKernel;

namespace POS.Sales.Domain;

/// <summary>
/// One item on a sale, with every pricing input snapshotted.
/// </summary>
/// <remarks>
/// Holds no navigation property into Catalog. The variant id is a loose reference;
/// everything needed to reprint the receipt or explain the price is copied here at
/// the moment of sale. This is deliberate duplication: catalog data is mutable and a
/// sale is a historical fact, so any live reference would let a price list edit
/// rewrite what a customer was charged last March.
/// </remarks>
public sealed class SaleLine : Entity<Guid>
{
    private readonly List<PriceAdjustment> _adjustments = [];

    private SaleLine() { }

    public static SaleLine Create(
        Guid variantId,
        string description,
        decimal quantity,
        string unitOfMeasure,
        Money unitPrice,
        string taxCode,
        decimal taxRate,
        bool taxInclusivePricing,
        Money unitCostAtSale,
        Guid? priceListId,
        int? priceListVersion) => new()
    {
        Id = SequentialId.New(),
        VariantId = variantId,
        Description = description,
        Quantity = quantity,
        UnitOfMeasure = unitOfMeasure,
        UnitPrice = unitPrice,
        TaxCode = taxCode,
        TaxRate = taxRate,
        TaxInclusivePricing = taxInclusivePricing,
        UnitCostAtSale = unitCostAtSale,
        PriceListId = priceListId,
        PriceListVersion = priceListVersion,
        DiscountAmount = Money.Zero(unitPrice.Currency),
        NetAmount = Money.Zero(unitPrice.Currency),
        TaxAmount = Money.Zero(unitPrice.Currency),
        GrossAmount = Money.Zero(unitPrice.Currency)
    };

    public int LineNumber { get; private set; }
    public Guid VariantId { get; private set; }
    public string Description { get; private set; } = null!;

    public decimal Quantity { get; private set; }
    public string UnitOfMeasure { get; private set; } = null!;

    public Money UnitPrice { get; private set; }
    public string TaxCode { get; private set; } = null!;
    public decimal TaxRate { get; private set; }

    /// <summary>True when <see cref="UnitPrice"/> already contains tax.</summary>
    public bool TaxInclusivePricing { get; private set; }

    /// <summary>
    /// Weighted average cost at the moment of sale, captured for margin reporting.
    /// </summary>
    /// <remarks>
    /// Snapshotted rather than looked up later. The running average changes with every
    /// receipt, so computing historical margin from today's cost would silently
    /// restate last quarter's profitability every time a delivery is booked in.
    /// </remarks>
    public Money UnitCostAtSale { get; private set; }

    public Guid? PriceListId { get; private set; }
    public int? PriceListVersion { get; private set; }

    public Money DiscountAmount { get; private set; }
    public Money NetAmount { get; private set; }
    public Money TaxAmount { get; private set; }
    public Money GrossAmount { get; private set; }

    public string Currency => UnitPrice.Currency;

    /// <summary>Ordered trace of every pricing stage that touched this line.</summary>
    /// <remarks>
    /// Exists so a manager can answer "why was this 4.37?" without reading code. The
    /// trace is persisted with the sale; a pricing engine whose output cannot be
    /// explained at the counter generates disputes nobody can resolve. See ADR 034.
    /// </remarks>
    public IReadOnlyList<PriceAdjustment> Adjustments => _adjustments.AsReadOnly();

    internal void AssignLineNumber(int lineNumber) => LineNumber = lineNumber;

    internal void RecordAdjustment(PriceAdjustment adjustment) => _adjustments.Add(adjustment);

    internal void ApplyTotals(Money discount, Money net, Money tax, Money gross)
    {
        DiscountAmount = discount;
        NetAmount = net;
        TaxAmount = tax;
        GrossAmount = gross;
    }

    /// <summary>Margin in money terms, computed from the snapshotted cost.</summary>
    public Money Margin => NetAmount - (UnitCostAtSale * Quantity);
}

/// <summary>One traceable step in the pricing pipeline.</summary>
public sealed record PriceAdjustment(
    int Sequence,
    PricingStage Stage,
    string Description,
    Money Amount,
    Guid? SourceId = null,
    Guid? AuthorisedBy = null)
{
    /// <summary>
    /// Required by EF Core for materialisation, and non-public so application code
    /// still has to go through the positional constructor.
    /// </summary>
    /// <remarks>
    /// EF Core 9 cannot bind a complex-typed parameter — <see cref="Money"/> — to a
    /// constructor, so it must be able to build the instance empty and write the
    /// properties through their backing fields. Chaining to the primary constructor
    /// rather than leaving the members unassigned keeps <c>Description</c> non-null,
    /// which is what its declared type promises.
    ///
    /// This is the same accommodation <see cref="Entity{TId}"/> documents, and it is
    /// the only concession to persistence in this file: no attribute, no base type, no
    /// reference to anything outside the domain.
    /// </remarks>
    private PriceAdjustment()
        : this(0, PricingStage.BasePrice, string.Empty, default, null, null)
    {
    }
}

public enum PricingStage
{
    BasePrice = 0,
    LineDiscount = 1,
    Promotion = 2,
    OrderDiscount = 3,
    Coupon = 4,
    Tax = 5,
    Rounding = 6
}

/// <summary>
/// The human-facing, gap-free fiscal receipt number.
/// </summary>
/// <remarks>
/// Deliberately separate from the <see cref="Sale"/> identifier. That id is machine identity —
/// UUID v7, assigned offline, never displayed. The receipt number is a legal artefact
/// with legal rules: gap-free within its series, human-readable, and per terminal so
/// that a disconnected till can still allocate one without coordination (ADR 005).
/// Conflating the two produces a system that satisfies neither requirement.
/// </remarks>
public sealed record ReceiptNumber(string Series, long Sequence)
{
    public override string ToString() => $"{Series}-{Sequence:D6}";
}

/// <summary>A single payment against a sale.</summary>
/// <remarks>
/// Split tender is the norm, not an edge case — cash plus card, gift card plus cash,
/// loyalty points plus card. Modelling payment as a collection from the start avoids
/// the retrofit that a single nullable PaymentMethod column always forces.
/// </remarks>
public sealed class Tender : Entity<Guid>
{
    private Tender() { }

    public static Tender Create(
        TenderMethod method,
        Money amount,
        DateTimeOffset takenAt,
        string? reference = null,
        Guid? paymentId = null) => new()
    {
        Id = SequentialId.NewAt(takenAt),
        Method = method,
        Amount = amount,
        TakenAt = takenAt,
        Reference = reference,
        PaymentId = paymentId
    };

    public TenderMethod Method { get; private set; }
    public Money Amount { get; private set; }
    public DateTimeOffset TakenAt { get; private set; }

    /// <summary>Masked card PAN, gift card number, or voucher code. Never full card data.</summary>
    public string? Reference { get; private set; }

    /// <summary>Loose reference to the Payments module, added in Phase 6.</summary>
    public Guid? PaymentId { get; private set; }
}

public enum TenderMethod
{
    Cash = 0,
    Card = 1,
    GiftCard = 2,
    LoyaltyPoints = 3,
    StoreCredit = 4,
    Voucher = 5,
    BankTransfer = 6
}

public static class TenderMethodExtensions
{
    /// <summary>
    /// Whether more than the balance may be taken, with the excess returned as change.
    /// </summary>
    /// <remarks>
    /// Cash only. Accepting an overtender on a card and handing back cash is a
    /// classic money-laundering and refund-fraud pattern, and card scheme rules
    /// prohibit it. On a gift card it converts a restricted instrument into cash.
    /// </remarks>
    public static bool AllowsOvertender(this TenderMethod method) => method == TenderMethod.Cash;

    /// <summary>Whether this tender requires connectivity to authorise.</summary>
    /// <remarks>
    /// Drives what a terminal may accept while offline. Gift cards and loyalty are
    /// balance-bearing instruments held centrally; redeeming them offline invites
    /// double-spend across terminals, so they are refused rather than optimistically
    /// accepted. Cash always works, which is why offline retail works at all.
    /// </remarks>
    public static bool RequiresConnectivity(this TenderMethod method) => method switch
    {
        TenderMethod.Cash => false,
        TenderMethod.Card => false, // offline card floor limits are a Phase 6 decision
        TenderMethod.GiftCard => true,
        TenderMethod.LoyaltyPoints => true,
        TenderMethod.StoreCredit => true,
        TenderMethod.Voucher => true,
        TenderMethod.BankTransfer => true,
        _ => true
    };
}

public static class SalesErrors
{
    public static Error SaleNotOpen(SaleStatus status) => Error.BusinessRule(
        "sales.sale_not_open",
        $"This sale is {status} and can no longer be modified. A completed sale is " +
        "corrected by a refund or void document, never by editing it.");

    public static Error SaleNotSuspended(SaleStatus status) => Error.BusinessRule(
        "sales.sale_not_suspended", $"Only a suspended sale can be resumed; this one is {status}.");

    public static Error EmptySale => Error.BusinessRule(
        "sales.empty_sale", "A sale must have at least one line before it can be completed.");

    public static Error LineNotFound(int lineNumber) => Error.NotFound(
        "sales.line_not_found", $"No line {lineNumber} on this sale.");

    public static Error TooManyLines(int max) => Error.BusinessRule(
        "sales.too_many_lines", $"A sale cannot exceed {max} lines.");

    public static Error CurrencyMismatch(string expected, string actual) => Error.Validation(
        "sales.currency_mismatch", $"This sale is in {expected}; received {actual}.");

    public static Error UnderTendered(Money balance) => Error.BusinessRule(
        "sales.under_tendered", $"Outstanding balance of {balance} must be paid before completion.");

    public static Error OvertenderNotAllowed(TenderMethod method) => Error.BusinessRule(
        "sales.overtender_not_allowed",
        $"{method} cannot be tendered above the balance due. Only cash may produce change.");

    public static Error CannotSuspendWithTenders => Error.BusinessRule(
        "sales.cannot_suspend_with_tenders",
        "A sale that has already taken payment cannot be suspended.");

    public static Error OnlyCompletedSalesCanBeVoided(SaleStatus status) => Error.BusinessRule(
        "sales.void.not_completed",
        $"Only a completed sale can be voided; this sale is {status}. An open basket " +
        "is cancelled, not voided — the two are different financial facts.");

    public static Error TenderRequiresConnectivity(TenderMethod method) => Error.BusinessRule(
        "sales.tender_requires_connectivity",
        $"{method} cannot be accepted while this terminal is offline, because its " +
        "balance is held centrally and offline redemption risks double-spend.");
}
