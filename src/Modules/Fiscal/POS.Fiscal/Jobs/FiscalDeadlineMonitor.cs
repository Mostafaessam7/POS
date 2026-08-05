using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using POS.Common.Jobs;
using POS.Fiscal.Pipeline;
using POS.SharedKernel;

namespace POS.Fiscal.Jobs;

/// <summary>
/// Watches for fiscal documents that have passed their statutory transmission deadline.
/// </summary>
/// <remarks>
/// <para>
/// In a clearance regime a fiscal document must reach the tax authority within a
/// statutory window. A document that sits past that window is not a queue entry to be
/// processed later — it is a REGULATORY EXPOSURE for the operator, accruing whether or
/// not anyone is looking. This monitor is the looking: it surfaces overdue documents so
/// they become an alarm a human sees rather than a fine an auditor finds.
/// </para>
/// <para>
/// It only reports; it does not transmit. Transmission is a separate concern with a
/// separate failure mode (a rejected document is different from an unsent one), and
/// conflating "chase the deadline" with "do the sending" would hide a stuck transmitter
/// behind a busy monitor. What it surfaces is the same set
/// <see cref="IFiscalDocumentStore.GetOverdueAsync"/> returns, which is cross-tenant by
/// design — an overdue document is the platform operator's problem regardless of whose
/// tenant issued it.
/// </para>
/// <para>
/// For a country with no clearance mandate — the GENERIC profile, where a document is
/// terminal the moment it is issued — nothing is ever given a deadline, so this monitor
/// correctly finds nothing. It is not idle: it is the standing proof that no document is
/// quietly overdue.
/// </para>
/// </remarks>
public sealed class FiscalDeadlineMonitor(
    IFiscalDocumentStore store,
    IClock clock,
    ILogger<FiscalDeadlineMonitor> logger)
{
    /// <summary>Finds every overdue document and raises it in the log.</summary>
    /// <returns>The overdue document ids, for a caller (or a test) that wants them.</returns>
    public async Task<IReadOnlyList<Guid>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var overdue = await store.GetOverdueAsync(now, cancellationToken);

        if (overdue.Count == 0)
            return [];

        // Error, not Warning: an overdue fiscal document is a compliance breach in
        // progress, not a soft signal. It should page someone, and logging it at Error
        // is what makes an alerting rule trivial to write against.
        foreach (var document in overdue)
        {
            logger.LogError(
                "Fiscal document {FormattedNumber} ({DocumentId}) is overdue for transmission. "
                + "Company {CompanyId}, due {DueBy}, status {Status}.",
                document.FormattedNumber,
                document.Id,
                document.CompanyId,
                document.TransmissionDueBy,
                document.Status);
        }

        return [.. overdue.Select(d => d.Id)];
    }
}

/// <summary>Runs the fiscal deadline check on an interval.</summary>
public sealed class FiscalDeadlineMonitorJob(
    IServiceScopeFactory scopeFactory,
    FiscalJobOptions options,
    ILogger<FiscalDeadlineMonitorJob> logger)
    : PeriodicJob<FiscalDeadlineMonitor>(scopeFactory, logger)
{
    protected override TimeSpan Interval => options.DeadlineCheckInterval;

    protected override string JobName => "fiscal-deadline-monitor";

    protected override Task RunAsync(FiscalDeadlineMonitor worker, CancellationToken cancellationToken) =>
        worker.CheckAsync(cancellationToken);
}

/// <summary>Timings for the Fiscal module's background jobs.</summary>
/// <remarks>
/// A minute by default. A statutory deadline is measured in hours, so a check every
/// minute detects an approaching breach with room to act, without polling the table
/// hard enough to matter.
/// </remarks>
public sealed class FiscalJobOptions
{
    public const string SectionName = "Fiscal:Jobs";

    public TimeSpan DeadlineCheckInterval { get; init; } = TimeSpan.FromMinutes(1);
}
