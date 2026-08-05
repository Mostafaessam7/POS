using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// A party we buy from.
/// </summary>
/// <remarks>
/// Scoped to the tenant and the company, not the branch. A supplier relationship — the
/// account number, the credit terms, the tax registration — belongs to the legal entity
/// that signed the contract. Branches raise orders against it; they do not own it.
///
/// Deliberately <em>not</em> merged with a future Customer entity into a generic
/// "business partner". The two share a name and an address and nothing else that matters:
/// suppliers have lead times and purchase terms, customers have credit limits and price
/// lists, and the generic version ends up as a bag of nullable columns where half are
/// meaningless for any given row.
/// </remarks>
public sealed class Supplier : AggregateRoot<Guid>, ITenantScoped, ICompanyScoped
{
    private readonly List<SupplierProductCode> _productCodes = [];

    private Supplier() { }

    public static Supplier Create(
        Guid tenantId,
        Guid companyId,
        string code,
        string name,
        string currency,
        SupplierTerms terms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(terms);

        return new Supplier
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            CompanyId = companyId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Currency = currency,
            Terms = terms,
            IsActive = true
        };
    }

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }

    /// <summary>Human-facing short code, unique per company.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Tax registration number, where the jurisdiction requires one on purchase documents.</summary>
    public string? TaxRegistrationNumber { get; private set; }

    /// <summary>
    /// The currency this supplier invoices in.
    /// </summary>
    /// <remarks>
    /// Held on the supplier rather than chosen per order because getting it wrong is a
    /// silent, expensive error: a purchase order raised in the wrong currency looks
    /// entirely plausible until the invoice arrives at roughly four times the expected
    /// figure. Orders inherit it and may not override it.
    /// </remarks>
    public string Currency { get; private set; } = string.Empty;

    public SupplierTerms Terms { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public IReadOnlyList<SupplierProductCode> ProductCodes => _productCodes;

    /// <summary>Optimistic concurrency token — terms and product codes are edited by humans, concurrently.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public void UpdateTerms(SupplierTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        Terms = terms;
    }

    public void SetTaxRegistration(string? number) => TaxRegistrationNumber = string.IsNullOrWhiteSpace(number) ? null : number.Trim();

    /// <summary>
    /// Deactivates the supplier. Existing orders are unaffected.
    /// </summary>
    /// <remarks>
    /// Deactivation, never deletion. A supplier is referenced by every purchase order,
    /// receipt and invoice we ever raised against them, and those are financial records
    /// (D6). Removing the row would orphan years of history to save one row.
    /// </remarks>
    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>
    /// Registers the supplier's own code for one of our variants.
    /// </summary>
    /// <remarks>
    /// This mapping is what makes a purchase order legible to the person picking the
    /// goods at the other end. It is also what lets a receipt be booked from the
    /// supplier's despatch note without a human translating part numbers, which is
    /// exactly the step that produces mis-bookings.
    ///
    /// One code per variant per supplier. The reverse is not constrained: suppliers do
    /// reuse a single pack code across several of our variants, and rejecting that would
    /// be modelling our preferences rather than their catalogue.
    /// </remarks>
    public Result AddProductCode(Guid variantId, string supplierCode, decimal packSize, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(supplierCode))
        {
            return Result.Failure(PurchasingErrors.SupplierCodeRequired);
        }

        if (packSize <= 0m)
        {
            return Result.Failure(PurchasingErrors.PackSizeMustBePositive);
        }

        if (_productCodes.Any(c => c.VariantId == variantId))
        {
            return Result.Failure(PurchasingErrors.DuplicateSupplierProductCode);
        }

        _productCodes.Add(new SupplierProductCode(
            variantId,
            supplierCode.Trim(),
            packSize,
            description?.Trim()));

        return Result.Success();
    }

    public void RemoveProductCode(Guid variantId) => _productCodes.RemoveAll(c => c.VariantId == variantId);

    public SupplierProductCode? FindProductCode(Guid variantId) =>
        _productCodes.FirstOrDefault(c => c.VariantId == variantId);
}

/// <summary>
/// Commercial terms agreed with a supplier.
/// </summary>
/// <remarks>
/// A value object rather than loose columns because these travel together: they are
/// agreed together, renegotiated together, and copied onto a purchase order together.
/// A purchase order snapshots them at the moment it is raised — see
/// <see cref="PurchaseOrder"/> — so that renegotiating terms next quarter does not
/// silently restate what last quarter's orders were placed under.
/// </remarks>
public sealed record SupplierTerms
{
    public SupplierTerms(int paymentTermDays, int leadTimeDays, decimal minimumOrderValue = 0m)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(paymentTermDays);
        ArgumentOutOfRangeException.ThrowIfNegative(leadTimeDays);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumOrderValue);

        PaymentTermDays = paymentTermDays;
        LeadTimeDays = leadTimeDays;
        MinimumOrderValue = minimumOrderValue;
    }

    /// <summary>Days from invoice date to payment due date. Zero means payment on delivery.</summary>
    public int PaymentTermDays { get; }

    /// <summary>
    /// Working days from order to expected delivery.
    /// </summary>
    /// <remarks>
    /// Used to derive an expected delivery date on the order, which in turn drives the
    /// overdue-delivery report. Stored as a plain day count rather than a calendar
    /// calculation because supplier lead times are quoted that way and because a
    /// working-day calculation needs a holiday calendar per country — real, but a
    /// different problem, and not one to solve implicitly inside a value object.
    /// </remarks>
    public int LeadTimeDays { get; }

    /// <summary>Order value below which the supplier will not ship. Advisory: warns, does not block.</summary>
    public decimal MinimumOrderValue { get; }

    public static SupplierTerms Default { get; } = new(paymentTermDays: 30, leadTimeDays: 7);
}

/// <summary>
/// How one supplier refers to one of our variants, and in what pack quantity.
/// </summary>
/// <param name="VariantId">Our variant, not theirs.</param>
/// <param name="Code">The supplier's code for it, as printed on their paperwork.</param>
/// <param name="PackSize">
/// Our units per one of the supplier's order units. Ordering "10" of a case of 24 means
/// 240 units arriving. Modelled explicitly because the alternative — expecting buyers to
/// do that multiplication in their heads — reliably produces order quantities out by a
/// factor of the case size.
/// </param>
/// <param name="Description">The supplier's own description, kept for order documents.</param>
public sealed record SupplierProductCode(
    Guid VariantId,
    string Code,
    decimal PackSize,
    string? Description);
