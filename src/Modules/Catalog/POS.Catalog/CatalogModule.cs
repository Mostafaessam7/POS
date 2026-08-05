using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Catalog.Persistence;
using POS.Catalog.Sync;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Sync.Pull;

namespace POS.Catalog;

/// <summary>Catalog's composition. The host calls this; it never reaches inside the module.</summary>
/// <remarks>
/// One registration entry point per module is what keeps <c>Program.cs</c> from
/// becoming the place where every module's internals are known. A module can add a
/// service, split a context, or change a lifetime without the host being edited.
/// </remarks>
public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<CatalogDbContext>((provider, options) =>
            options.UsePosSqlServer<CatalogDbContext>(
                connectionString,
                CatalogDbContext.MigrationsHistoryTable,
                CatalogDbContext.Schema)
                .AddPosInterceptors(provider));

        // Registered here, not in Sync — see IMasterDataSource's remarks. Sync
        // knows a "Product" source exists; it never knows Catalog does.
        services.AddScoped<IMasterDataSource, ProductMasterDataSource>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class CatalogDbContextFactory : PosDesignTimeDbContextFactory<CatalogDbContext>
{
    protected override string MigrationsHistoryTable => CatalogDbContext.MigrationsHistoryTable;

    protected override string Schema => CatalogDbContext.Schema;

    protected override CatalogDbContext Create(
        DbContextOptions<CatalogDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
