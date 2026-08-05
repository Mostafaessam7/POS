using POS.SharedKernel;

namespace POS.Catalog.Domain;

/// <summary>
/// The abstract sellable concept — "Men's Oxford Shirt".
/// </summary>
/// <remarks>
/// EVERY product has at least one variant, including simple ones like a can of
/// beans. Special-casing "products without variants" produces two code paths
/// through pricing, stock, barcodes, and reporting FOREVER, and the second path is
/// always the buggy one. The cost of a single-variant wrapper is one row; the cost
/// of the dual model is permanent. See ADR 019.
/// </remarks>
public sealed class Product : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private readonly List<ProductVariant> _variants = [];

    private Product() { }

    public static Product Create(
        string name,
        Guid categoryId,
        Guid? brandId,
        Guid unitOfMeasureId,
        Guid taxGroupId,
        DateTimeOffset occurredAt,
        ProductKind kind = ProductKind.Stocked)
    {
        var product = new Product
        {
            Id = SequentialId.New(),
            Name = name,
            CategoryId = categoryId,
            BrandId = brandId,
            UnitOfMeasureId = unitOfMeasureId,
            TaxGroupId = taxGroupId,
            Kind = kind,
            IsActive = true
        };

        product.Raise(new ProductCreated(product.Id, name) { OccurredAt = occurredAt });
        return product;
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid? BrandId { get; private set; }
    public Guid UnitOfMeasureId { get; private set; }
    public Guid TaxGroupId { get; private set; }
    public ProductKind Kind { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyList<ProductVariant> Variants => _variants.AsReadOnly();

    public Result<ProductVariant> AddVariant(string sku, string variantName, Money defaultPrice, DateTimeOffset occurredAt)
    {
        if (_variants.Any(v => v.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase)))
            return Error.Conflict("catalog.sku.duplicate", $"SKU '{sku}' already exists on this product.");

        var variant = ProductVariant.Create(Id, sku, variantName, defaultPrice);
        _variants.Add(variant);

        Raise(new ProductVariantAdded(Id, variant.Id, sku) { OccurredAt = occurredAt });
        return variant;
    }

    public void Rename(string name, DateTimeOffset occurredAt)
    {
        Name = name;
        Raise(new ProductRenamed(Id, name) { OccurredAt = occurredAt });
    }

    /// <summary>
    /// Deactivates rather than deletes.
    /// </summary>
    /// <remarks>
    /// Historical sale lines reference this product. Hard deletion breaks every
    /// report that joins to it — which is also why sale lines snapshot the
    /// description and price rather than joining here at read time.
    /// </remarks>
    public void Deactivate(DateTimeOffset occurredAt)
    {
        IsActive = false;
        Raise(new ProductDeactivated(Id) { OccurredAt = occurredAt });
    }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

public enum ProductKind
{
    /// <summary>Ordinary stock-tracked goods.</summary>
    Stocked = 0,

    /// <summary>Sold by weight or measure. Quantity is decimal and may arrive from the barcode.</summary>
    Weighed = 1,

    /// <summary>Labour, delivery, warranty. No stock movements.</summary>
    Service = 2,

    /// <summary>A fixed collection sold as one line. Components deplete stock individually.</summary>
    Bundle = 3
}

/// <summary>
/// The actual stock-keeping unit — "Men's Oxford Shirt, Blue, Large".
/// </summary>
/// <remarks>
/// Stock, barcodes, and prices attach HERE, never to Product. This is the entity
/// the rest of the system transacts against.
/// </remarks>
public sealed class ProductVariant : Entity<Guid>, ITenantScoped
{
    private readonly List<VariantAttributeValue> _attributes = [];

    private ProductVariant() { }

    internal static ProductVariant Create(Guid productId, string sku, string name, Money defaultPrice) => new()
    {
        Id = SequentialId.New(),
        ProductId = productId,
        Sku = sku,
        Name = name,
        DefaultPriceAmount = defaultPrice.Amount,
        DefaultPriceCurrency = defaultPrice.Currency,
        IsActive = true
    };

    public Guid TenantId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>
    /// Fallback price when no price list applies. Stored as amount + currency
    /// rather than an owned Money type so it can be indexed and queried directly.
    /// </summary>
    public decimal DefaultPriceAmount { get; private set; }

    public string DefaultPriceCurrency { get; private set; } = null!;

    /// <summary>
    /// Most recent purchase cost. Maintained by the Inventory module's costing
    /// policy — weighted average per ADR 020 — and used for margin reporting only.
    /// It is NEVER the basis for a selling price.
    /// </summary>
    public decimal? AverageCost { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyList<VariantAttributeValue> Attributes => _attributes.AsReadOnly();

    public Money DefaultPrice => new(DefaultPriceAmount, DefaultPriceCurrency);

    public void SetAttribute(Guid attributeId, Guid attributeValueId)
    {
        _attributes.RemoveAll(a => a.AttributeId == attributeId);
        _attributes.Add(new VariantAttributeValue(Id, attributeId, attributeValueId));
    }

    internal void UpdateAverageCost(decimal cost) => AverageCost = cost;
}

/// <summary>
/// Variant axes as a typed model rather than a JSON blob.
/// </summary>
/// <remarks>
/// JSON is tempting and wrong: merchants filter and report by size and colour, and
/// "show me every Large in Blue across the chain" against a JSON column is either a
/// full scan or a computed-column workaround. Typed costs two join tables.
/// </remarks>
public sealed class VariantAttribute : Entity<Guid>, ITenantScoped
{
    private VariantAttribute() { }

    public static VariantAttribute Create(string name, int displayOrder) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        DisplayOrder = displayOrder
    };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public int DisplayOrder { get; private set; }
}

public sealed class VariantAttributeOption : Entity<Guid>, ITenantScoped
{
    private VariantAttributeOption() { }

    public static VariantAttributeOption Create(Guid attributeId, string value, int displayOrder) => new()
    {
        Id = SequentialId.New(),
        AttributeId = attributeId,
        Value = value,
        DisplayOrder = displayOrder
    };

    public Guid TenantId { get; private set; }
    public Guid AttributeId { get; private set; }
    public string Value { get; private set; } = null!;
    public int DisplayOrder { get; private set; }
}

public sealed record VariantAttributeValue(Guid VariantId, Guid AttributeId, Guid AttributeOptionId);

public sealed record ProductCreated(Guid ProductId, string Name) : DomainEvent;
public sealed record ProductVariantAdded(Guid ProductId, Guid VariantId, string Sku) : DomainEvent;
public sealed record ProductDeactivated(Guid ProductId) : DomainEvent;
public sealed record ProductRenamed(Guid ProductId, string Name) : DomainEvent;
