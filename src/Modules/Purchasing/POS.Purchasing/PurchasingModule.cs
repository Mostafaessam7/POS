using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Purchasing.Persistence;
using POS.Purchasing.Posting;

namespace POS.Purchasing;

/// <summary>Purchasing's composition. The host calls this; it never reaches inside the module.</summary>
public static class PurchasingModule
{
    public static IServiceCollection AddPurchasingModule(
        this IServiceCollection services,
        string connectionString,
        PurchasingPolicyOptions? policy = null)
    {
        services.AddDbContext<PurchasingDbContext>((provider, options) =>
            options.UsePosSqlServer<PurchasingDbContext>(
                connectionString,
                PurchasingDbContext.MigrationsHistoryTable,
                PurchasingDbContext.Schema)
                .AddPosInterceptors(provider));

        // Defaults are deliberate rather than empty: an unconfigured deployment gets a
        // working approval ladder that requires approval, not one that waves everything
        // through. See PurchasingPolicyOptions for why this is configuration and not
        // yet tenant data.
        services.AddSingleton(policy ?? new PurchasingPolicyOptions());

        // Resolves the deployment default above against a tenant's own override, if
        // any — see PurchasingPolicyResolver. Scoped because it depends (through
        // ITenantSettingsDirectory) on a scoped DbContext.
        services.AddScoped<PurchasingPolicyResolver>();

        // Posting orchestration. These own the ordering between "the receipt is posted"
        // and "the stock moved" — see GoodsReceiptPostingService for why that ordering
        // is the design and not an implementation detail.
        services.AddScoped<GoodsReceiptPostingService>();
        services.AddScoped<SupplierReturnDispatchService>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class PurchasingDbContextFactory : PosDesignTimeDbContextFactory<PurchasingDbContext>
{
    protected override string MigrationsHistoryTable => PurchasingDbContext.MigrationsHistoryTable;

    protected override string Schema => PurchasingDbContext.Schema;

    protected override PurchasingDbContext Create(
        DbContextOptions<PurchasingDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
