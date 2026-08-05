using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Tenancy;
using POS.Sales.Persistence;
using POS.SharedKernel;
using POS.Sync.Contracts;
using POS.Sync.Ingest;
using POS.Sync.Pull;

namespace POS.Api.Endpoints;

/// <summary>Terminal upload, master-data pull, and the counters that prove it worked.</summary>
public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1").RequireAuthorization();

        // Idempotent by contract. A terminal that loses the response retries the
        // identical batch, and the unique constraint on (TerminalId, TerminalSequence)
        // collapses the replay into a no-op. This endpoint therefore returns 200 with
        // a report, not 201: "created" would be a lie on the second call.
        group.MapPost("/sync/batches", async (
            UploadBatchRequest request,
            SyncIngestService ingest,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            if (!tenantContext.IsResolved)
                return Error.Forbidden("sync.tenant_required", "A tenant is required.").ToHttpResult();

            var result = await ingest.IngestAsync(tenantContext.TenantId, request, ct);

            return result.Match(Results.Ok, error => error.ToHttpResult());
        });

        // The downward direction: a terminal asking for whatever master data it
        // needs to ring up a sale offline. See MasterDataPullService for why this is
        // a full snapshot today rather than the incremental delta the request's own
        // per-entity-type cursor is shaped for.
        group.MapPost("/sync/master-data/pull", async (
            PullMasterDataRequest request,
            MasterDataPullService pull,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            if (!tenantContext.IsResolved)
                return Error.Forbidden("sync.tenant_required", "A tenant is required.").ToHttpResult();

            var result = await pull.PullAsync(tenantContext.TenantId, request, ct);

            return result.Match(Results.Ok, error => error.ToHttpResult());
        });

        // Exists so a test can assert that a replayed batch produced no duplicate
        // sales — a count is the cheapest possible statement of that.
        group.MapGet("/sales/count", async (SalesDbContext db, CancellationToken ct) =>
            Results.Ok(new SalesCountResponse(await db.Sales.CountAsync(ct))));

        return app;
    }
}

public sealed record SalesCountResponse(int Count);
