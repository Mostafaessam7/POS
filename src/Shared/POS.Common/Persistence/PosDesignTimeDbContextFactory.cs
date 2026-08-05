using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using POS.Common.Tenancy;

namespace POS.Common.Persistence;

/// <summary>
/// Base for the design-time factories the EF Core tools use to build a model without
/// starting the application.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL: <c>dotnet ef</c> normally finds a context by booting the
/// host. Every context here takes an <see cref="ITenantContext"/> that only a live
/// request can satisfy, so booting the host to scaffold a migration would mean
/// resolving a tenant that does not exist. A design-time factory supplies a stub
/// instead. See ADR 002.
///
/// The stub tenant matters more than it looks. Query filters are baked into the model
/// and reference <c>tenantContext.TenantId</c>; the model builder must therefore be
/// able to READ that property while scaffolding. It is never used to filter anything
/// at design time — no query runs — but a throwing context here fails every migration
/// command with a confusing "no tenant resolved".
///
/// CONNECTION STRING RESOLUTION, in order:
///   1. the value passed after <c>--</c> on the command line
///   2. POS_CONNECTIONSTRINGS__POS / ConnectionStrings__Pos in the environment
///   3. appsettings.json beside the host, if one is found
///   4. a local SQL Server default, so a fresh clone can scaffold without setup
/// </remarks>
public abstract class PosDesignTimeDbContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : DbContext
{
    /// <summary>
    /// Used only when nothing else supplies a connection string. Scaffolding a
    /// migration does not connect, so this needs to be well-formed, not reachable.
    /// </summary>
    private const string LocalDefault =
        "Server=(localdb)\\MSSQLLocalDB;Database=PosPlatform;Trusted_Connection=true;TrustServerCertificate=true";

    protected abstract string MigrationsHistoryTable { get; }

    protected abstract string Schema { get; }

    protected abstract TContext Create(DbContextOptions<TContext> options, ITenantContext tenantContext);

    public TContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<TContext>();

        builder.UsePosSqlServer<TContext>(ResolveConnectionString(args), MigrationsHistoryTable, Schema);

        return Create(builder.Options, DesignTimeTenantContext.Instance);
    }

    private static string ResolveConnectionString(string[] args)
    {
        if (args is { Length: > 0 } && !string.IsNullOrWhiteSpace(args[0]))
            return args[0];

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetConnectionString(PosSqlServer.ConnectionStringName)
            ?? LocalDefault;
    }
}

/// <summary>
/// A tenant context for model building only. Reports a fixed, obviously-fake tenant.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Guid.Empty"/>: an all-zero id is what an uninitialised
/// field looks like, so if this ever leaked into a real query filter the resulting
/// rows would be indistinguishable from a bug. A recognisable constant shows up
/// immediately in a profiler trace as "someone is running design-time code in
/// production".
/// </remarks>
public sealed class DesignTimeTenantContext : ITenantContext
{
    public static DesignTimeTenantContext Instance { get; } = new();

    public Guid TenantId { get; } = new("DE519DE5-1900-0000-0000-000000000000");

    public bool IsResolved => true;

    public IDisposable BypassForSystemOperation(string reason) => NullScope.Instance;

    public IDisposable EnterTenant(Guid tenantId, string reason) => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
            // Nothing is ever filtered at design time, so there is nothing to restore.
        }
    }
}
