using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Sales.Persistence;
using POS.Sales.Pricing;
using POS.Sales.Sync;
using POS.Sync.Ingest;

namespace POS.Sales;

/// <summary>Sales' composition. The host calls this; it never reaches inside the module.</summary>
public static class SalesModule
{
    public static IServiceCollection AddSalesModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<SalesDbContext>((provider, options) =>
            options.UsePosSqlServer<SalesDbContext>(
                connectionString,
                SalesDbContext.MigrationsHistoryTable,
                SalesDbContext.Schema)
                .AddPosInterceptors(provider));

        // Stages are stateless and registered as singletons. Registration ORDER is
        // deliberately not load-bearing: PricingPipeline re-sorts whatever it is given
        // into CanonicalOrder, because a pricing pipeline whose behaviour depends on
        // the sequence of DI calls is one refactor away from taxing the undiscounted
        // subtotal and doing it silently.
        //
        // There is no Coupon stage. The stage exists in the enum and the pipeline
        // tolerates its absence; adding one is Phase 8 work, not composition.
        services.AddSingleton<IPricingStage, BasePriceStage>();
        services.AddSingleton<IPricingStage, LineDiscountStage>();
        services.AddSingleton<IPricingStage, PromotionStage>();
        services.AddSingleton<IPricingStage, OrderDiscountStage>();
        services.AddSingleton<IPricingStage, TaxStage>();
        services.AddSingleton<IPricingStage, CashRoundingStage>();

        services.AddSingleton(provider =>
            new PricingPipeline(provider.GetServices<IPricingStage>().ToList()));

        // Sales registers its OWN sync handler. Sync owns transport and must not know
        // what a "sale" record means — that inversion is what keeps it from acquiring a
        // project reference to every module (architecture rule 2).
        services.AddScoped<ISyncRecordHandler, SaleSyncHandler>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class SalesDbContextFactory : PosDesignTimeDbContextFactory<SalesDbContext>
{
    protected override string MigrationsHistoryTable => SalesDbContext.MigrationsHistoryTable;

    protected override string Schema => SalesDbContext.Schema;

    protected override SalesDbContext Create(
        DbContextOptions<SalesDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
