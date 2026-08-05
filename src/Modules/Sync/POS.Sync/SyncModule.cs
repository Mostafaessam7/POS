using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Sync.Ingest;
using POS.Sync.Persistence;
using POS.Sync.Pull;

namespace POS.Sync;

/// <summary>Sync's composition. The host calls this; it never reaches inside the module.</summary>
/// <remarks>
/// Note what is NOT registered here: any <see cref="ISyncRecordHandler"/>. Sync owns
/// transport and knows nothing about what a record means, so handlers are registered
/// by the module that owns the record type. That inversion is what keeps Sync from
/// acquiring a project reference to every module, which architecture rule 2 forbids.
/// </remarks>
public static class SyncModule
{
    public static IServiceCollection AddSyncModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<SyncDbContext>((provider, options) =>
            options.UsePosSqlServer<SyncDbContext>(
                connectionString,
                SyncDbContext.MigrationsHistoryTable,
                SyncDbContext.Schema)
                .AddPosInterceptors(provider));

        services.AddScoped<SyncIngestService>();

        // No IMasterDataSource registered here either, for the same reason no
        // ISyncRecordHandler is: each owning module registers its own (see e.g.
        // CatalogModule -> ProductMasterDataSource).
        services.AddScoped<MasterDataPullService>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class SyncDbContextFactory : PosDesignTimeDbContextFactory<SyncDbContext>
{
    protected override string MigrationsHistoryTable => SyncDbContext.MigrationsHistoryTable;

    protected override string Schema => SyncDbContext.Schema;

    protected override SyncDbContext Create(
        DbContextOptions<SyncDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
