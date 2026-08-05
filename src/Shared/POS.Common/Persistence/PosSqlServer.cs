using Microsoft.EntityFrameworkCore;

namespace POS.Common.Persistence;

/// <summary>
/// One definition of how this platform talks to SQL Server.
/// </summary>
/// <remarks>
/// Every module context is configured through here rather than each calling
/// <c>UseSqlServer</c> with its own options. The two settings that matter:
///
/// MIGRATIONS HISTORY TABLE — each module owns its own, in its own schema. This is
/// what lets Catalog ship a migration without blocking an Identity deployment, and
/// it is the seam along which a module could later move to its own database
/// (ADR 002). Sharing one <c>__EFMigrationsHistory</c> across nine contexts would
/// make every <c>dotnet ef</c> command in the repository lie about what is applied.
///
/// RETRY ON FAILURE — Azure SQL drops connections routinely under scale-out and
/// failover; a POS that surfaces those as failed sales is unusable. Note the
/// consequence documented on <see cref="ExecutionStrategyRetryCount"/>: with a
/// retrying strategy, user-initiated transactions must be wrapped in
/// <c>CreateExecutionStrategy().ExecuteAsync(...)</c> or EF throws. The stock ledger
/// is the caller that does this.
/// </remarks>
public static class PosSqlServer
{
    /// <summary>Name of the single connection string every module reads.</summary>
    public const string ConnectionStringName = "Pos";

    public const int ExecutionStrategyRetryCount = 5;

    public static readonly TimeSpan ExecutionStrategyMaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>Applies the platform's SQL Server settings for one module context.</summary>
    /// <typeparam name="TContext">
    /// The module context. Its assembly is where that module's migrations live, so
    /// <c>dotnet ef migrations add</c> run from the module directory does the obvious
    /// thing rather than writing into the host.
    /// </typeparam>
    /// <param name="options">The builder being configured.</param>
    /// <param name="connectionString">Target database. Shared by every module (ADR 002).</param>
    /// <param name="migrationsHistoryTable">The module's own history table name.</param>
    /// <param name="schema">The module's schema, which the history table also lives in.</param>
    public static DbContextOptionsBuilder UsePosSqlServer<TContext>(
        this DbContextOptionsBuilder options,
        string connectionString,
        string migrationsHistoryTable,
        string schema)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsHistoryTable(migrationsHistoryTable, schema);
            sql.MigrationsAssembly(typeof(TContext).Assembly.FullName);

            sql.EnableRetryOnFailure(
                maxRetryCount: ExecutionStrategyRetryCount,
                maxRetryDelay: ExecutionStrategyMaxDelay,
                errorNumbersToAdd: null);
        });
    }
}
