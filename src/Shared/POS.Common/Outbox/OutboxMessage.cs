namespace POS.Common.Outbox;

/// <summary>
/// A message queued for delivery, written in the SAME transaction as the state
/// change that produced it.
/// </summary>
/// <remarks>
/// This is what makes "the sale was recorded but the stock movement was never
/// published" impossible. Either both commit or neither does.
///
/// It is also the backbone of Phase 2 sync: a terminal writes the sale and its
/// outbox entry to SQLite atomically, and the sync worker drains the outbox. A
/// process kill mid-upload loses nothing, because nothing is removed from the
/// outbox until the server has acknowledged it.
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public static OutboxMessage Create(string type, string payload, Guid? tenantId, DateTimeOffset now) => new()
    {
        Id = SharedKernel.SequentialId.New(),
        Type = type,
        Payload = payload,
        TenantId = tenantId,
        OccurredAt = now,
        Status = OutboxStatus.Pending,
        AttemptCount = 0
    };

    public Guid Id { get; private set; }

    /// <summary>Fully-qualified contract type name. Versioned — see ADR 018.</summary>
    public string Type { get; private set; } = null!;

    public string Payload { get; private set; } = null!;
    public Guid? TenantId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public string? LastError { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = OutboxStatus.Processed;
        ProcessedAt = now;
        LastError = null;
    }

    /// <summary>
    /// Records a failure and schedules a retry with exponential backoff plus jitter.
    /// </summary>
    /// <remarks>
    /// Jitter matters more than it looks. Without it, every terminal in a chain that
    /// lost connectivity at the same moment retries at the same moment, and the
    /// recovering server is hit by a synchronised thundering herd.
    /// </remarks>
    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts = 12)
    {
        AttemptCount++;
        LastError = error.Length > 2000 ? error[..2000] : error;

        if (AttemptCount >= maxAttempts)
        {
            Status = OutboxStatus.DeadLettered;
            return;
        }

        var backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(AttemptCount, 10)));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5_000));
        NextAttemptAt = now.Add(backoff + jitter);
    }
}

public enum OutboxStatus
{
    Pending = 0,
    Processed = 1,

    /// <summary>Exhausted retries. Requires operator attention; never silently dropped.</summary>
    DeadLettered = 2
}
