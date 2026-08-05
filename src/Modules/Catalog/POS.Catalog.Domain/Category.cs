using POS.SharedKernel;

namespace POS.Catalog.Domain;

/// <summary>
/// A node in the merchandise hierarchy, stored as a materialised path.
/// </summary>
/// <remarks>
/// WHY NOT A NAIVE ParentId:
///
/// The dominant query is "every product in Menswear and all its descendants" — it
/// runs on the catalogue screen, in every category report, and in promotion
/// evaluation. With ParentId alone that needs a recursive CTE on every read.
///
/// A materialised path makes it a single indexed prefix scan:
///     WHERE Path LIKE '/electronics/audio/%'
///
/// The cost is that MOVING a subtree rewrites the path of every descendant. That is
/// rare (merchandise hierarchies are restructured seasonally at most) and is a
/// bounded, single-transaction update. Trading a rare expensive write for a
/// constant cheap read is the right way round here.
///
/// Considered and rejected: a closure table — more flexible, an extra table, and
/// more join complexity than this hierarchy's depth (rarely beyond four) justifies.
/// See ADR 022.
/// </remarks>
public sealed class Category : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private Category() { }

    public static Category CreateRoot(string name, string slug) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        Slug = slug,
        ParentId = null,
        Path = $"/{slug}/",
        Depth = 0
    };

    public static Category CreateChild(Category parent, string name, string slug) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        Slug = slug,
        ParentId = parent.Id,
        Path = $"{parent.Path}{slug}/",
        Depth = parent.Depth + 1
    };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>URL-safe segment. Immutable — the path depends on it.</summary>
    public string Slug { get; private set; } = null!;

    public Guid? ParentId { get; private set; }

    /// <summary>
    /// Slash-delimited ancestry, e.g. "/electronics/audio/headphones/".
    /// Leading and trailing slashes make prefix matching unambiguous — without them,
    /// "/audio" would also match "/audiobooks".
    /// </summary>
    public string Path { get; private set; } = null!;

    public int Depth { get; private set; }
    public int DisplayOrder { get; private set; }

    /// <summary>
    /// Recomputes this node's path after a move. The caller is responsible for
    /// applying the same operation to every descendant, in one transaction.
    /// </summary>
    public void Reparent(Category? newParent)
    {
        ParentId = newParent?.Id;
        Path = newParent is null ? $"/{Slug}/" : $"{newParent.Path}{Slug}/";
        Depth = newParent?.Depth + 1 ?? 0;
    }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

public sealed class Brand : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private Brand() { }

    public static Brand Create(string name) => new() { Id = SequentialId.New(), Name = name };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

/// <summary>
/// A unit in which a product is sold, with conversion to its base unit.
/// </summary>
/// <remarks>
/// Quantities are DECIMAL throughout, not integer. Weighed goods exist; 1.347 kg is
/// a normal sale line. Integer quantities are one of those decisions that appears
/// harmless in week one and requires a schema migration across every transaction
/// table in month nine.
/// </remarks>
public sealed class UnitOfMeasure : AggregateRoot<Guid>, ITenantScoped, ISoftDeletable
{
    private UnitOfMeasure() { }

    public static UnitOfMeasure CreateBase(string code, string name, bool allowsFractions) => new()
    {
        Id = SequentialId.New(),
        Code = code,
        Name = name,
        BaseUnitId = null,
        ConversionFactor = 1m,
        AllowsFractionalQuantity = allowsFractions
    };

    /// <summary>e.g. a Case of 12 Each: factor 12 against the Each base unit.</summary>
    public static UnitOfMeasure CreateDerived(string code, string name, Guid baseUnitId, decimal factor) => new()
    {
        Id = SequentialId.New(),
        Code = code,
        Name = name,
        BaseUnitId = baseUnitId,
        ConversionFactor = factor,
        AllowsFractionalQuantity = false
    };

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public Guid? BaseUnitId { get; private set; }
    public decimal ConversionFactor { get; private set; }

    /// <summary>False for discrete goods — selling 0.5 of a television is a data entry error.</summary>
    public bool AllowsFractionalQuantity { get; private set; }

    public decimal ToBaseQuantity(decimal quantity) => quantity * ConversionFactor;

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}
