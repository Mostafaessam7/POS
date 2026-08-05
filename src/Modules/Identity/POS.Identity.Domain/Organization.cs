using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A SaaS customer. THE security boundary.
/// </summary>
/// <remarks>
/// Deliberately not ITenantScoped — it IS the tenant. There is no
/// "super user who can see all tenants" in the application; internal support
/// access is a separate, audited, out-of-band mechanism. See ADR 006.
/// </remarks>
public sealed class Tenant : AggregateRoot<Guid>
{
    private Tenant() { }

    public static Tenant Create(string name, string subdomain, Guid? provisionedByOperatorId = null) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        Subdomain = subdomain.ToLowerInvariant(),
        Status = TenantStatus.Active,
        ProvisionedByOperatorId = provisionedByOperatorId
    };

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Selects the login page and branding ONLY. Never an authorization input —
    /// DNS is not authentication.
    /// </summary>
    public string Subdomain { get; private set; } = null!;

    public TenantStatus Status { get; private set; }

    /// <summary>
    /// Which named <see cref="ProvisioningOperator"/> called <c>POST /tenants</c>, if any.
    /// </summary>
    /// <remarks>
    /// Nullable because a tenant seeded outside the API (a data migration, a manual
    /// insert) has no operator to attribute — that is a real possibility this system
    /// does not try to rule out, so the audit trail says so honestly rather than
    /// pointing at a fabricated id. Not a foreign key: <see cref="ProvisioningOperator"/>
    /// rows may be pruned long after the tenants they created are still active, and this
    /// column's whole job is to outlive that.
    /// </remarks>
    public Guid? ProvisionedByOperatorId { get; private set; }

    public void Suspend() => Status = TenantStatus.Suspended;
}

public enum TenantStatus { Active = 0, Suspended = 1, Closed = 2 }

/// <summary>
/// A legal entity within a tenant. Own tax registration, own books, own invoice
/// number sequence.
/// </summary>
/// <remarks>
/// Separate from Tenant because a franchise group, or a retailer operating in two
/// countries, legitimately runs several legal entities under one account. Adding
/// this level now costs one column. Retrofitting it means backfilling a foreign key
/// across every financial record already issued, plus reconciling fiscal numbering.
/// See ADR 006.
/// </remarks>
public sealed class Company : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private Company() { }

    public static Company Create(
        string name,
        string legalName,
        string taxRegistration,
        string baseCurrency,
        string countryCode,
        string fiscalProfileCode = "GENERIC") => new()
    {
        Id = SequentialId.New(),
        Name = name,
        LegalName = legalName,
        TaxRegistrationNumber = taxRegistration,
        BaseCurrency = baseCurrency.ToUpperInvariant(),
        CountryCode = countryCode.ToUpperInvariant(),
        FiscalProfileCode = fiscalProfileCode.ToUpperInvariant()
    };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string LegalName { get; private set; } = null!;
    public string TaxRegistrationNumber { get; private set; } = null!;

    /// <summary>ISO 3166-1 alpha-2. The jurisdiction whose tax law this entity trades under.</summary>
    /// <remarks>
    /// Lives on Company rather than Tenant or Branch because the taxable person is the
    /// legal entity. A group operating in three countries has three companies, three
    /// registrations, and three fiscal profiles inside one tenant — which is exactly
    /// why Company was separated from Tenant in Phase 1 before any customer asked for it.
    /// </remarks>
    public string CountryCode { get; private set; } = null!;

    /// <summary>
    /// Selects the fiscal profile plugin. Deliberately NOT derived from
    /// <see cref="CountryCode"/> alone.
    /// </summary>
    /// <remarks>
    /// Country does not determine fiscal regime one-for-one. A jurisdiction may run
    /// several regimes concurrently (a small-taxpayer exemption, a phased mandate
    /// where only enrolled entities are live, a free-zone entity outside the mainland
    /// regime), and mandates switch on a date rather than at a border. Keeping this a
    /// separate, explicit code means onboarding a company onto a new regime is a
    /// configuration change, not a deployment. Defaults to GENERIC.
    /// </remarks>
    public string FiscalProfileCode { get; private set; } = "GENERIC";

    /// <summary>Reporting currency. Transactions may be tendered in others.</summary>
    public string BaseCurrency { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

/// <summary>A physical store.</summary>
public sealed class Branch : AggregateRoot<Guid>, ITenantScoped, ICompanyScoped, IAuditable, ISoftDeletable
{
    private Branch() { }

    public static Branch Create(Guid companyId, string code, string name, string timeZoneId, int businessDayStartHour) => new()
    {
        Id = SequentialId.New(),
        CompanyId = companyId,
        Code = code,
        Name = name,
        TimeZoneId = timeZoneId,
        BusinessDayStartHour = businessDayStartHour
    };

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }

    /// <summary>Short numeric code used in receipt numbering. Immutable once trading.</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>IANA time zone. Required to derive the business date correctly.</summary>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>
    /// Hour at which the trading day rolls over. A 24-hour store set to 4 books
    /// anything before 04:00 to the previous business day. See BusinessDate.
    /// </summary>
    public int BusinessDayStartHour { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

/// <summary>
/// A stock-holding location: shop floor, stockroom, van, or transit.
/// </summary>
/// <remarks>
/// A branch commonly maps to several warehouses. Modelling stock at branch level
/// makes it impossible to represent "on the shop floor" versus "in the back",
/// which is exactly the distinction a stock transfer needs.
/// </remarks>
public sealed class Warehouse : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private Warehouse() { }

    public static Warehouse Create(Guid branchId, string code, string name, WarehouseKind kind) => new()
    {
        Id = SequentialId.New(),
        BranchId = branchId,
        Code = code,
        Name = name,
        Kind = kind
    };

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public WarehouseKind Kind { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

public enum WarehouseKind
{
    SalesFloor = 0,
    StockRoom = 1,
    Transit = 2,

    /// <summary>Damaged or quarantined goods. Not sellable, but must remain visible.</summary>
    Quarantine = 3
}
