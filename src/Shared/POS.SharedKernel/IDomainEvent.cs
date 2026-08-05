namespace POS.SharedKernel;

/// <summary>
/// Something that has happened in the domain, expressed in the past tense.
/// </summary>
/// <remarks>
/// Raised inside an aggregate, dispatched only AFTER the transaction commits.
/// Dispatching before commit means a handler can observe — and act on — a state
/// change that is subsequently rolled back.
/// </remarks>
public interface IDomainEvent
{
    public Guid EventId { get; }
    public DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Base for domain events.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OccurredAt"/> is deliberately REQUIRED rather than defaulted to
/// <c>DateTimeOffset.UtcNow</c>. A default would have been convenient and wrong,
/// for three reasons:
/// </para>
/// <list type="number">
///   <item>It reintroduces ambient time into the one place — the domain — that
///   ADR 017 exists to keep deterministic, and no test could then freeze it.</item>
///   <item>An event's occurrence time is a business fact, not a wall-clock
///   reading. A sale completed offline at 22:14 and synced at 07:02 the next
///   morning occurred at 22:14; a default captures the wrong one of the two.</item>
///   <item>The default is evaluated at construction, so an event replayed from
///   the outbox or reconstituted during sync would silently re-stamp itself.</item>
/// </list>
/// <para>
/// Aggregates therefore take <see cref="IClock"/> on the operation that raises
/// the event, or accept the business timestamp from the caller. The compiler
/// enforces this: <c>required</c> means an event cannot be constructed without
/// a deliberate answer to "when did this happen?".
/// </para>
/// </remarks>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = SequentialId.New();

    public required DateTimeOffset OccurredAt { get; init; }
}
