using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Contracts.Inventory;
using POS.Inventory.Adjustments;
using POS.Inventory.Costing;
using POS.Inventory.Integration;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.Inventory.Stocktakes;
using POS.Inventory.Transfers;

namespace POS.Inventory;

/// <summary>Inventory's composition. The host calls this; it never reaches inside the module.</summary>
public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services,
        string connectionString,
        InventoryPolicyOptions? policy = null)
    {
        services.AddDbContext<InventoryDbContext>((provider, options) =>
            options.UsePosSqlServer<InventoryDbContext>(
                connectionString,
                InventoryDbContext.MigrationsHistoryTable,
                InventoryDbContext.Schema)
                .AddPosInterceptors(provider));

        services.AddScoped<IStockLedger, SqlServerStockLedger>();

        // The port other modules post stock through. Registered by Inventory and
        // consumed through POS.Contracts, so nothing outside this module ever sees
        // IStockLedger or a StockMovement.
        services.AddScoped<IStockPostingPort, StockPostingAdapter>();

        // The hand-driven write path, for the host's inventory endpoints.
        services.AddScoped<StockAdjustmentService>();
        services.AddScoped<IStockBalanceRebuilder, StockBalanceRebuilder>();

        // Transfers and stocktakes are multi-step workflows that post to the ledger
        // directly (not through IStockPostingPort — see each service's remarks).
        services.AddScoped<StockTransferService>();
        services.AddScoped<StocktakeService>();

        // Defaults are deliberate rather than empty, the same stance
        // PurchasingPolicyOptions takes: an unconfigured deployment gets a working
        // variance write-off ladder that escalates with value, not one that waves
        // everything through.
        services.AddSingleton(policy ?? new InventoryPolicyOptions());

        // Resolves the deployment default above against a tenant's own override, if
        // any — see InventoryPolicyResolver. Scoped because it depends (through
        // ITenantSettingsDirectory) on a scoped DbContext.
        services.AddScoped<InventoryPolicyResolver>();

        // Weighted average is the platform default and the only implementation
        // (ADR 020). Registered through the interface anyway, because the costing
        // method is per-tenant configuration the day a customer needs FIFO.
        services.AddSingleton<ICostingPolicy, WeightedAverageCostingPolicy>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class InventoryDbContextFactory : PosDesignTimeDbContextFactory<InventoryDbContext>
{
    protected override string MigrationsHistoryTable => InventoryDbContext.MigrationsHistoryTable;

    protected override string Schema => InventoryDbContext.Schema;

    protected override InventoryDbContext Create(
        DbContextOptions<InventoryDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
