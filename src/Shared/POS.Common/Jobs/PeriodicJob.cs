using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace POS.Common.Jobs;

/// <summary>
/// A background job that runs a scoped unit of work on a fixed interval.
/// </summary>
/// <remarks>
/// <para>
/// Three things every recurring maintenance job in this system has to get right, in one
/// place so no individual job has to remember them:
/// </para>
/// <list type="number">
///   <item>A FRESH SCOPE PER TICK. The job itself is a singleton — it lives for the
///   life of the process — but the work touches DbContexts and other scoped services.
///   Resolving those from the root provider would share one DbContext across every tick
///   forever, which leaks memory and eventually serves stale tracked entities. Each tick
///   gets its own scope, disposed when it finishes.</item>
///   <item>THE LOOP NEVER DIES. A job that throws on one bad tick and stops is worse
///   than useless: the failure is silent and the maintenance simply never happens again
///   until someone restarts the process. Every tick's exception is caught and logged,
///   and the timer keeps ticking. Cancellation is the one exception that IS allowed to
///   stop it, because that is the host shutting down.</item>
///   <item>NO OVERLAP. <see cref="PeriodicTimer"/> waits <em>after</em> the work
///   finishes, so a slow tick delays the next one rather than stacking a second copy on
///   top of it. A sweep that runs long under load must not have three of itself
///   contending for the same rows.</item>
/// </list>
/// <para>
/// The work is expressed as a scoped service (<typeparamref name="TWorker"/>) with an
/// <c>ExecuteAsync</c>-shaped method, not as an override here, so the same logic can be
/// invoked directly in a test without waiting on a timer. That is the difference between
/// a background job that is tested and one that is merely hoped to work.
/// </para>
/// </remarks>
public abstract class PeriodicJob<TWorker>(
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
    where TWorker : notnull
{
    /// <summary>How often the job runs. Read once at start-up.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>A short name for logs. The job's own, not the worker's.</summary>
    protected abstract string JobName { get; }

    /// <summary>Runs one tick's work against a resolved worker.</summary>
    protected abstract Task RunAsync(TWorker worker, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Background job {JobName} started; interval {Interval}.", JobName, Interval);

        using var timer = new PeriodicTimer(Interval);

        // Run once at start rather than waiting a whole interval first: a job whose
        // period is an hour should not leave the first hour of every deployment
        // unswept.
        do
        {
            await TickAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Background job {JobName} stopping.", JobName);
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var worker = scope.ServiceProvider.GetRequiredService<TWorker>();
            await RunAsync(worker, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down. Let the loop's WaitAsync end it cleanly.
        }
#pragma warning disable CA1031 // One bad tick must not kill the job forever — see the
        // class remarks. The exception is logged; the next tick runs regardless.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Background job {JobName} tick failed; it will run again next interval.", JobName);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
