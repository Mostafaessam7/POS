using POS.SharedKernel;

namespace POS.Catalog.Domain;

/// <summary>
/// A versioned, date-effective set of prices, optionally scoped to a branch or
/// customer group.
/// </summary>
/// <remarks>
/// Prices are VERSIONED and date-effective, never overwritten. Three reasons:
///
///  1. A reprinted receipt from six months ago must show the price actually charged.
///  2. Promotions are scheduled in advance; a merchant sets next week's prices today.
///  3. Margin reporting needs the price that applied on the day, not today's.
///
/// The sale line SNAPSHOTS the resolved price and records which price list version
/// produced it, so "why was this £4.37?" is answerable years later. Reporting must
/// never join to a live price. See ADR 023.
/// </remarks>
public sealed class PriceList : AggregateRoot<Guid>, ITenantScoped, IAuditable
{
    private readonly List<PriceListEntry> _entries = [];

    private PriceList() { }

    public static PriceList Create(
        string name,
        string currency,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo,
        int priority,
        Guid? branchId = null,
        Guid? customerGroupId = null) => new()
        {
            Id = SequentialId.New(),
            Name = name,
            Currency = currency.ToUpperInvariant(),
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Priority = priority,
            BranchId = branchId,
            CustomerGroupId = customerGroupId,
            Version = 1
        };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Currency { get; private set; } = null!;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }

    /// <summary>Higher wins where several lists apply. Branch overrides beat chain defaults.</summary>
    public int Priority { get; private set; }

    /// <summary>Null means chain-wide.</summary>
    public Guid? BranchId { get; private set; }

    public Guid? CustomerGroupId { get; private set; }

    /// <summary>
    /// Incremented on every change. Recorded on the sale line so a historical price
    /// can be traced to its exact source. This is the audit trail that makes pricing
    /// disputes resolvable.
    /// </summary>
    public int Version { get; private set; }

    public IReadOnlyList<PriceListEntry> Entries => _entries.AsReadOnly();

    public bool IsEffectiveAt(DateTimeOffset moment) =>
        EffectiveFrom <= moment && (EffectiveTo is null || EffectiveTo > moment);

    public void SetPrice(Guid variantId, Money price)
    {
        if (price.Currency != Currency)
            throw new InvalidOperationException(
                $"Price currency {price.Currency} does not match list currency {Currency}.");

        _entries.RemoveAll(e => e.VariantId == variantId);
        _entries.Add(new PriceListEntry(Id, variantId, price.Amount));
        Version++;
    }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
}

public sealed record PriceListEntry(Guid PriceListId, Guid VariantId, decimal Amount);

/// <summary>
/// A tax treatment, versioned by effective date.
/// </summary>
/// <remarks>
/// Rate changes are legislated in advance and apply from a specific instant. A
/// mutable rate column means a rate change silently restates every historical
/// report — and returns processed after the change would refund at the wrong rate.
/// Returns must use the ORIGINAL transaction's rate, which is why the sale line
/// snapshots it.
/// </remarks>
public sealed class TaxGroup : AggregateRoot<Guid>, ITenantScoped, IAuditable
{
    private readonly List<TaxRate> _rates = [];

    private TaxGroup() { }

    public static TaxGroup Create(string name, string code) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        Code = code
    };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Code { get; private set; } = null!;

    public IReadOnlyList<TaxRate> Rates => _rates.AsReadOnly();

    public void AddRate(decimal percentage, DateTimeOffset effectiveFrom, bool isInclusive)
    {
        var superseded = _rates.SingleOrDefault(r => r.EffectiveTo is null);
        superseded?.SupersedeAt(effectiveFrom);

        _rates.Add(TaxRate.Create(Id, percentage, effectiveFrom, isInclusive));
    }

    public TaxRate? RateAt(DateTimeOffset moment) =>
        _rates.SingleOrDefault(r => r.EffectiveFrom <= moment && (r.EffectiveTo is null || r.EffectiveTo > moment));

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
}

public sealed class TaxRate : Entity<Guid>, ITenantScoped
{
    private TaxRate() { }

    internal static TaxRate Create(Guid taxGroupId, decimal percentage, DateTimeOffset from, bool isInclusive) => new()
    {
        Id = SequentialId.New(),
        TaxGroupId = taxGroupId,
        Percentage = percentage,
        EffectiveFrom = from,
        IsInclusive = isInclusive
    };

    public Guid TenantId { get; private set; }
    public Guid TaxGroupId { get; private set; }

    /// <summary>Whole percentage points, e.g. 14.0 for 14%.</summary>
    public decimal Percentage { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }

    /// <summary>
    /// True where the shelf price already contains tax. This is a JURISDICTIONAL
    /// convention, not a preference: most of Europe and the Middle East display
    /// tax-inclusive prices; North America displays exclusive. It changes how the
    /// line total is derived, so it must be recorded per rate.
    /// </summary>
    public bool IsInclusive { get; private set; }

    internal void SupersedeAt(DateTimeOffset moment) => EffectiveTo = moment;

    /// <summary>Extracts the tax component from a gross or net amount as appropriate.</summary>
    public Money TaxOn(Money amount) => IsInclusive
        ? new Money(amount.Amount - amount.Amount / (1 + Percentage / 100m), amount.Currency).Round()
        : new Money(amount.Amount * Percentage / 100m, amount.Currency).Round();
}
