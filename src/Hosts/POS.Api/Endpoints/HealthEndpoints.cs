using Microsoft.EntityFrameworkCore;
using POS.Catalog.Persistence;
using POS.Expenses.Persistence;
using POS.Fiscal.Persistence;
using POS.Identity.Persistence;
using POS.Inventory.Persistence;
using POS.Payments.Persistence;
using POS.Purchasing.Persistence;
using POS.Sales.Persistence;
using POS.Sync.Persistence;

namespace POS.Api.Endpoints;

/// <summary>Liveness and readiness.</summary>
/// <remarks>
/// The two are separated because an orchestrator does different things with them:
/// a failed LIVENESS check restarts the process, a failed READINESS check only takes
/// the instance out of the load balancer. Wiring a database check into liveness — the
/// obvious shortcut, since one endpoint is simpler than two — means a brief database
/// failover restarts every API instance simultaneously, turning a ten-second blip
/// into a cold-start stampede.
///
/// Both are anonymous. A health endpoint behind authentication cannot be probed by
/// the thing that needs to probe it, and neither response discloses anything beyond
/// which module could not be reached.
/// </remarks>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
           .AllowAnonymous()
           .DisableRateLimiting()
           .WithName("Liveness");

        app.MapGet("/health/ready", async (
            IdentityDbContext identity,
            CatalogDbContext catalog,
            InventoryDbContext inventory,
            SalesDbContext sales,
            PaymentsDbContext payments,
            FiscalDbContext fiscal,
            PurchasingDbContext purchasing,
            ExpensesDbContext expenses,
            SyncDbContext sync,
            CancellationToken ct) =>
        {
            // Every module context, and it must stay every module context. A readiness
            // probe that checks a subset reports healthy while the module it forgot is
            // unreachable, which is worse than having no probe: the instance stays in
            // the load balancer and fails real requests.
            var modules = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["identity"] = await identity.Database.CanConnectAsync(ct),
                ["catalog"] = await catalog.Database.CanConnectAsync(ct),
                ["inventory"] = await inventory.Database.CanConnectAsync(ct),
                ["sales"] = await sales.Database.CanConnectAsync(ct),
                ["payments"] = await payments.Database.CanConnectAsync(ct),
                ["fiscal"] = await fiscal.Database.CanConnectAsync(ct),
                ["purchasing"] = await purchasing.Database.CanConnectAsync(ct),
                ["expenses"] = await expenses.Database.CanConnectAsync(ct),
                ["sync"] = await sync.Database.CanConnectAsync(ct)
            };

            var ready = modules.Values.All(reachable => reachable);

            return ready
                ? Results.Ok(new { status = "ready", modules })
                : Results.Json(new { status = "degraded", modules }, statusCode: 503);
        })
        .AllowAnonymous()
        .DisableRateLimiting()
        .WithName("Readiness");

        return app;
    }
}
