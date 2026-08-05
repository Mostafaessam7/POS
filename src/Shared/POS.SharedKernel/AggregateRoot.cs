namespace POS.SharedKernel;

/// <summary>
/// The consistency boundary. Everything reachable from an aggregate root is
/// loaded, validated, and saved together in one transaction.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() { }

    /// <summary>
    /// Exposed as read-only so callers cannot append events from outside the
    /// aggregate, which would bypass its invariants — architecture rule 7.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Called by the dispatcher after events have been published post-commit.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
