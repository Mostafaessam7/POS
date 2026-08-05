namespace POS.SharedKernel;

/// <summary>Base type for entities with identity-based equality.</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IEquatable<TId>
{
    protected Entity(TId id) => Id = id;

    /// <summary>
    /// Required by EF Core for materialisation. Non-public so application code
    /// cannot construct an entity in an invalid state — architecture rule 6.
    /// </summary>
    protected Entity() { }

    public TId Id { get; protected set; }

    /// <summary>
    /// True when this instance has no assigned identity yet.
    /// </summary>
    /// <remarks>
    /// Under ADR 005 identity is assigned at construction (UUID v7), never by the
    /// database, so this should be rare. It is handled anyway because the failure
    /// mode is silent and nasty: without it, two distinct transient entities both
    /// hold <c>default(TId)</c>, compare EQUAL, and collapse into a single element
    /// when added to a <c>HashSet</c> or an aggregate's child collection. One of
    /// the two lines on a sale simply vanishes, with no exception anywhere.
    /// </remarks>
    private bool IsTransient => Id.Equals(default(TId));

    public bool Equals(Entity<TId>? other)
    {
        if (other is null || GetType() != other.GetType())
        {
            return false;
        }

        // Two entities without identity are only the same entity if they are
        // literally the same object.
        if (IsTransient || other.IsTransient)
        {
            return ReferenceEquals(this, other);
        }

        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    /// <remarks>
    /// Transient entities fall back to reference identity so that the hash agrees
    /// with <see cref="Equals(Entity{TId}?)"/>. A hash that disagrees with equality
    /// corrupts every dictionary and set the entity is placed in.
    /// </remarks>
    public override int GetHashCode() =>
        IsTransient
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)
            : HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
