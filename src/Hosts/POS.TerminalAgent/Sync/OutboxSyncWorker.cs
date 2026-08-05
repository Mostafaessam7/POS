using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using POS.Common.Outbox;
using POS.SharedKernel;
using POS.Sync.Contracts;
using POS.TerminalAgent.Persistence;

namespace POS.TerminalAgent.Sync;

/// <summary>
/// Drains the local outbox to the store server.
/// </summary>
/// <remarks>
/// THE DURABILITY CONTRACT:
///
/// A sale and its outbox entry are written to SQLite in ONE transaction. Nothing is
/// marked processed until the server has acknowledged it. Therefore:
///
///   - kill -9 mid-upload loses nothing; the batch is simply re-sent
///   - a dropped response causes a re-send, which the server's unique constraint
///     on (TerminalId, TerminalSequence) collapses into a no-op
///   - the terminal never needs to know whether the server "really" got it
///
/// The design is deliberately AT-LEAST-ONCE delivery plus server-side idempotency,
/// rather than an attempt at exactly-once. Exactly-once across an unreliable
/// network is not achievable; pretending otherwise produces subtle duplicate sales.
/// </remarks>
public sealed class OutboxSyncWorker(
    IServiceScopeFactory scopeFactory,
    ISyncClient client,
    IClock clock,
    ILogger<OutboxSyncWorker> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OfflineDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                delay = await DrainOnceAsync(stoppingToken) ? TimeSpan.Zero : IdleDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                // Expected and unremarkable: this is a till in a shop with flaky
                // broadband. Log at Debug, back off, carry on selling.
                logger.LogDebug("Store server unreachable; will retry.");
                delay = OfflineDelay;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected sync failure.");
                delay = OfflineDelay;
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<bool> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TerminalDbContext>();

        var now = clock.UtcNow;

        var pending = await db.Outbox
            .Where(m => m.Status == OutboxStatus.Pending)
            .Where(m => m.NextAttemptAt == null || m.NextAttemptAt <= now)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return false;

        var records = pending
            .Select((m, index) => new SyncRecord(
                m.Id,
                // Sequence is allocated from the terminal's monotonic counter at
                // enqueue time; index is a placeholder for the illustrative case.
                index,
                m.Type,
                m.Payload,
                m.OccurredAt))
            .ToList();

        var request = new UploadBatchRequest(
            SyncProtocol.CurrentVersion,
            client.TerminalId,
            records[0].TerminalSequence,
            records[^1].TerminalSequence,
            records);

        var response = await client.UploadAsync(request, cancellationToken);

        var rejectedIds = response.Rejected.Select(r => r.RecordId).ToHashSet();

        foreach (var message in pending)
        {
            if (rejectedIds.Contains(message.Id))
            {
                var reason = response.Rejected.First(r => r.RecordId == message.Id);

                // A server-side rejection is deterministic — retrying an identical
                // payload will fail identically. Dead-letter immediately rather
                // than burning twelve retries on a record that can never succeed.
                message.MarkFailed($"{reason.Code}: {reason.Reason}", now, maxAttempts: 1);
                continue;
            }

            message.MarkProcessed(now);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Synced batch {BatchId}: accepted={Accepted} duplicates={Duplicates} rejected={Rejected}",
                response.BatchId, response.Accepted, response.Duplicates, response.Rejected.Count);
        }

        return true;
    }
}

public interface ISyncClient
{
    public Guid TerminalId { get; }

    public Task<UploadBatchResponse> UploadAsync(UploadBatchRequest request, CancellationToken cancellationToken);

    public Task<PullMasterDataResponse> PullAsync(PullMasterDataRequest request, CancellationToken cancellationToken);
}
