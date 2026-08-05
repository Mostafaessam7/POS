using FluentValidation;
using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Security;
using POS.Common.Validation;
using POS.Identity.Authorization;
using POS.Inventory;
using POS.Inventory.Adjustments;
using POS.Inventory.Domain;
using POS.Inventory.Ledger;
using POS.Inventory.Persistence;
using POS.Inventory.Stocktakes;
using POS.Inventory.Transfers;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// The Inventory API: stock levels, the movement ledger, and manual adjustments.
/// </summary>
/// <remarks>
/// <para>
/// Reads dominate. A balance is a materialised row (ADR 026) and is served directly;
/// the movement ledger is the append-only audit trail behind it and answers "why is the
/// number what it is". Neither is reconstructed on read — the whole costing design
/// exists so that the balance is authoritative and cheap.
/// </para>
/// <para>
/// Most stock moves as a consequence of business events — a receipt posted, a sale
/// synced — through the module ports, never by editing a number. The manual adjustment
/// is the one fully hand-driven write: a wastage write-off or a found-stock correction,
/// reason-coded and gated on its own permission. Transfers and stocktakes are
/// multi-step workflows of their own, routed below through
/// <see cref="StockTransferService"/> and <see cref="StocktakeService"/>, but they are
/// still, ultimately, hand-driven writes — every leg is reason-coded or userId-attributed
/// the same way.
/// </para>
/// </remarks>
public static class InventoryEndpoints
{
    private const int MaxPageSize = 200;

    private static readonly Error ScopeDenied = Error.Forbidden(
        "inventory.scope_denied",
        "You do not hold that permission at this warehouse.");

    /// <summary>The variance write-off ladder, lowest first — mirrors Purchasing's approval ladder.</summary>
    private static readonly (string PermissionCode, ApprovalLevel Level)[] VarianceWriteOffLadder =
    [
        (Permissions.Inventory.TransferWriteOffVarianceSupervisor, ApprovalLevel.Supervisor),
        (Permissions.Inventory.TransferWriteOffVarianceManager, ApprovalLevel.Manager),
        (Permissions.Inventory.TransferWriteOffVarianceDirector, ApprovalLevel.Director)
    ];

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/inventory").RequireAuthorization();

        // Stock on hand across a warehouse, most valuable first. Paged, because a large
        // catalogue's balance table is not something to return whole.
        group.MapGet("/warehouses/{warehouseId:guid}/balances", async (
            Guid warehouseId,
            int? skip,
            int? take,
            InventoryDbContext db,
            CancellationToken ct) =>
        {
            var page = Math.Clamp(take ?? 50, 1, MaxPageSize);
            var offset = Math.Max(skip ?? 0, 0);

            var balances = await db.StockBalances
                .AsNoTracking()
                .Where(b => b.WarehouseId == warehouseId)
                .OrderByDescending(b => b.TotalValue.Amount)
                .Skip(offset)
                .Take(page)
                .Select(b => ToResponse(b))
                .ToListAsync(ct);

            return Results.Ok(balances);
        })
        .RequirePermission(Permissions.Inventory.View);

        // A single variant's balance. 404 when the variant has never moved in this
        // warehouse — there is genuinely no balance, which is distinct from a balance of
        // zero (a variant that moved and came back to nothing).
        group.MapGet("/warehouses/{warehouseId:guid}/balances/{variantId:guid}", async (
            Guid warehouseId,
            Guid variantId,
            InventoryDbContext db,
            CancellationToken ct) =>
        {
            var balance = await db.StockBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.WarehouseId == warehouseId && b.VariantId == variantId, ct);

            return balance is null ? Results.NotFound() : Results.Ok(ToResponse(balance));
        })
        .RequirePermission(Permissions.Inventory.View);

        // The audit trail for one variant, newest first. This is what
        // IX_StockMovements_Balance is for.
        group.MapGet("/warehouses/{warehouseId:guid}/variants/{variantId:guid}/movements", async (
            Guid warehouseId,
            Guid variantId,
            int? take,
            InventoryDbContext db,
            CancellationToken ct) =>
        {
            var page = Math.Clamp(take ?? 100, 1, MaxPageSize);

            var movements = await db.StockMovements
                .AsNoTracking()
                .Where(m => m.WarehouseId == warehouseId && m.VariantId == variantId)
                .OrderByDescending(m => m.OccurredAt)
                .Take(page)
                .Select(m => new StockMovementResponse(
                    m.Id,
                    m.Type.ToString(),
                    m.QuantityDelta,
                    m.UnitCost.Amount,
                    m.Reference.DocumentType.ToString(),
                    m.Reference.DocumentId,
                    m.ReasonCode,
                    m.OccurredAt,
                    m.BusinessDate))
                .ToListAsync(ct);

            return Results.Ok(movements);
        })
        .RequirePermission(Permissions.Inventory.View);

        // The negative-stock exception report. Negative balances are permitted (ADR 027)
        // but they are an operational signal — stock sold ahead of its receipt, or a
        // count that has drifted — and IX_StockBalances_Negative keeps this cheap.
        group.MapGet("/warehouses/{warehouseId:guid}/negative", async (
            Guid warehouseId,
            InventoryDbContext db,
            CancellationToken ct) =>
        {
            var negative = await db.StockBalances
                .AsNoTracking()
                .Where(b => b.WarehouseId == warehouseId && b.QuantityOnHand < 0m)
                .OrderBy(b => b.QuantityOnHand)
                .Select(b => ToResponse(b))
                .ToListAsync(ct);

            return Results.Ok(new { warehouseId, count = negative.Count, balances = negative });
        })
        .RequirePermission(Permissions.Inventory.View);

        // Stock ledger self-check: rebuild every balance in a warehouse from its
        // movements and report any divergence. The report the stock-balance
        // reconciliation endpoint wraps, exposed here for an operator working a single
        // warehouse.
        group.MapGet("/warehouses/{warehouseId:guid}/reconcile", async (
            Guid warehouseId,
            IStockBalanceRebuilder rebuilder,
            CancellationToken ct) =>
        {
            var divergences = await rebuilder.ReconcileAsync(warehouseId, ct);

            return Results.Ok(new
            {
                warehouseId,
                isClean = divergences.Count == 0,
                divergences = divergences.Select(d => new
                {
                    d.VariantId,
                    d.StoredQuantity,
                    d.LedgerQuantity,
                    d.QuantityDifference
                })
            });
        })
        .RequirePermission(Permissions.Inventory.View);

        // The one hand-driven write. Reason-coded, scoped to the warehouse, and routed
        // through the ledger like every other movement — an adjustment is a movement,
        // not an edit to a balance.
        group.MapPost("/adjustments", async (
            CreateAdjustmentRequest request,
            StockAdjustmentService adjustments,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Inventory.AdjustmentCreate, request.WarehouseId, ct))
                return ScopeDenied.ToHttpResult();

            var result = await adjustments.AdjustAsync(
                new StockAdjustmentRequest
                {
                    AdjustmentId = Guid.CreateVersion7(),
                    WarehouseId = request.WarehouseId,
                    VariantId = request.VariantId,
                    Kind = request.Kind,
                    Quantity = request.Quantity,
                    UnitCost = request.Kind == StockAdjustmentKind.Increase
                        ? new Money(request.UnitCost!.Value, request.Currency!)
                        : null,
                    ReasonCode = request.ReasonCode,
                    BusinessDate = request.BusinessDate,
                    Notes = request.Notes,
                    UserId = currentUser.UserId
                },
                ct);

            return result.Match(
                balance => Results.Ok(ToResponse(balance)),
                error => error.ToHttpResult());
        })
        .AddValidation<CreateAdjustmentRequest>()
        .RequirePermission(Permissions.Inventory.AdjustmentCreate);

        MapTransfers(group);
        MapStocktakes(group);

        return app;
    }

    private static void MapTransfers(IEndpointRouteBuilder group)
    {
        group.MapPost("/transfers", async (
            CreateStockTransferRequest request,
            StockTransferService transfers,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Inventory.TransferCreate, request.SourceWarehouseId, ct))
                return ScopeDenied.ToHttpResult();

            var result = await transfers.CreateAsync(
                new POS.Inventory.Transfers.CreateStockTransferRequest(
                    request.SourceWarehouseId,
                    request.DestinationWarehouseId,
                    request.InTransitWarehouseId,
                    request.TransferNumber,
                    currentUser.UserId ?? Guid.Empty,
                    [.. request.Lines.Select(l => new CreateStockTransferLine(l.VariantId, l.Quantity))]),
                ct);

            return result.Match(
                transfer => Results.Created($"/api/v1/inventory/transfers/{transfer.Id}", ToResponse(transfer)),
                error => error.ToHttpResult());
        })
        .AddValidation<CreateStockTransferRequest>()
        .RequirePermission(Permissions.Inventory.TransferCreate);

        group.MapGet("/transfers/{id:guid}", async (Guid id, InventoryDbContext db, CancellationToken ct) =>
        {
            var transfer = await db.StockTransfers
                .AsNoTracking()
                .Include(t => t.Lines)
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            return transfer is null ? Results.NotFound() : Results.Ok(ToResponse(transfer));
        })
        .RequirePermission(Permissions.Inventory.View);

        // Dispatch moves the sent quantities from the source warehouse into in-transit.
        // Scoped on the source, because that is the warehouse whose staff are releasing
        // stock they are accountable for.
        group.MapPost("/transfers/{id:guid}/dispatch", async (
            Guid id,
            InventoryDbContext db,
            StockTransferService transfers,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var sourceWarehouseId = await db.StockTransfers
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => (Guid?)t.SourceWarehouseId)
                .FirstOrDefaultAsync(ct);

            if (sourceWarehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.TransferCreate, sourceWarehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await transfers.DispatchAsync(id, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                transfer => Results.Ok(ToResponse(transfer)),
                error => error.ToHttpResult());
        })
        .RequirePermission(Permissions.Inventory.TransferCreate);

        // Receipt is scoped on the destination — the warehouse whose staff are taking
        // accountability for what actually arrived.
        group.MapPost("/transfers/{id:guid}/receive", async (
            Guid id,
            ReceiveStockTransferRequest request,
            InventoryDbContext db,
            StockTransferService transfers,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var destinationWarehouseId = await db.StockTransfers
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => (Guid?)t.DestinationWarehouseId)
                .FirstOrDefaultAsync(ct);

            if (destinationWarehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.TransferReceive, destinationWarehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var received = request.ReceivedQuantities.ToDictionary(l => l.VariantId, l => l.Quantity);

            var result = await transfers.ReceiveAsync(id, received, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                transfer => Results.Ok(ToResponse(transfer)),
                error => error.ToHttpResult());
        })
        .RequirePermission(Permissions.Inventory.TransferReceive);

        // Writing off a shortfall is the one step that disposes of stock nobody can
        // account for, so who may do it depends on how much it's worth — the same
        // approve-by-value-ladder shape as Purchasing's order approval. No
        // RequirePermission for a single code: the ladder IS the gate, exactly as it is
        // for /orders/{id}/approve — a user holding none of the three levels gets 403
        // from the scope check below.
        group.MapPost("/transfers/{id:guid}/write-off-variance", async (
            Guid id,
            WriteOffVarianceRequest request,
            InventoryDbContext db,
            StockTransferService transfers,
            InventoryPolicyResolver policyResolver,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var inTransitWarehouseId = await db.StockTransfers
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => (Guid?)t.InTransitWarehouseId)
                .FirstOrDefaultAsync(ct);

            if (inTransitWarehouseId is null)
                return Results.NotFound();

            var level = await scope.HighestHeldAsync(VarianceWriteOffLadder, inTransitWarehouseId.Value, ct);

            if (level is null)
                return ScopeDenied.ToHttpResult();

            // Whether this level is senior enough for the variance's value, and
            // whether the receiver is allowed to write off their own find, are both
            // the aggregate's decisions (mirrors PurchaseOrder.Approve).
            var policy = await policyResolver.ResolveAsync(db.CurrentTenantId, ct);
            var result = await transfers.WriteOffVarianceAsync(
                id, policy, currentUser.UserId ?? Guid.Empty, level.Value, request.ReasonCode, ct);

            return result.Match(
                transfer => Results.Ok(ToResponse(transfer)),
                error => error.ToHttpResult());
        })
        .AddValidation<WriteOffVarianceRequest>();

        // Cancellation is scoped on the source, same as create/dispatch: only reachable
        // while the transfer is still a draft, so it's the same actor who would dispatch
        // it deciding it no longer should be.
        group.MapPost("/transfers/{id:guid}/cancel", async (
            Guid id,
            CancelStockTransferRequest request,
            InventoryDbContext db,
            StockTransferService transfers,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var sourceWarehouseId = await db.StockTransfers
                .AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => (Guid?)t.SourceWarehouseId)
                .FirstOrDefaultAsync(ct);

            if (sourceWarehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.TransferCreate, sourceWarehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await transfers.CancelAsync(id, request.Reason, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                transfer => Results.Ok(ToResponse(transfer)),
                error => error.ToHttpResult());
        })
        .AddValidation<CancelStockTransferRequest>()
        .RequirePermission(Permissions.Inventory.TransferCreate);
    }

    private static void MapStocktakes(IEndpointRouteBuilder group)
    {
        group.MapPost("/stocktakes", async (
            StartStocktakeApiRequest request,
            StocktakeService stocktakes,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Inventory.CountPerform, request.WarehouseId, ct))
                return ScopeDenied.ToHttpResult();

            var result = await stocktakes.StartAsync(
                new StartStocktakeRequest(
                    request.WarehouseId,
                    request.StocktakeNumber,
                    request.Scope,
                    request.IsBlind,
                    currentUser.UserId ?? Guid.Empty,
                    request.BusinessDate),
                ct);

            return result.Match(
                stocktake => Results.Created($"/api/v1/inventory/stocktakes/{stocktake.Id}", ToResponse(stocktake)),
                error => error.ToHttpResult());
        })
        .AddValidation<StartStocktakeApiRequest>()
        .RequirePermission(Permissions.Inventory.CountPerform);

        group.MapGet("/stocktakes/{id:guid}", async (Guid id, InventoryDbContext db, CancellationToken ct) =>
        {
            var stocktake = await db.Stocktakes
                .AsNoTracking()
                .Include(s => s.Lines)
                .FirstOrDefaultAsync(s => s.Id == id, ct);

            return stocktake is null ? Results.NotFound() : Results.Ok(ToResponse(stocktake));
        })
        .RequirePermission(Permissions.Inventory.View);

        group.MapPost("/stocktakes/{id:guid}/counts", async (
            Guid id,
            RecordCountRequest request,
            InventoryDbContext db,
            StocktakeService stocktakes,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var warehouseId = await db.Stocktakes
                .AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => (Guid?)s.WarehouseId)
                .FirstOrDefaultAsync(ct);

            if (warehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.CountPerform, warehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await stocktakes.RecordCountAsync(
                id, request.VariantId, request.CountedQuantity, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                stocktake => Results.Ok(ToResponse(stocktake)),
                error => error.ToHttpResult());
        })
        .AddValidation<RecordCountRequest>()
        .RequirePermission(Permissions.Inventory.CountPerform);

        group.MapPost("/stocktakes/{id:guid}/complete-counting", async (
            Guid id,
            InventoryDbContext db,
            StocktakeService stocktakes,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var warehouseId = await db.Stocktakes
                .AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => (Guid?)s.WarehouseId)
                .FirstOrDefaultAsync(ct);

            if (warehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.CountPerform, warehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await stocktakes.CompleteCountingAsync(id, ct);

            return result.Match(
                stocktake => Results.Ok(ToResponse(stocktake)),
                error => error.ToHttpResult());
        })
        .RequirePermission(Permissions.Inventory.CountPerform);

        // Posting is the step that turns variance into a ledger movement, so it carries
        // the approval permission rather than the one that merely performs a count — the
        // same separation Purchasing draws between raising and approving.
        group.MapPost("/stocktakes/{id:guid}/post", async (
            Guid id,
            InventoryDbContext db,
            StocktakeService stocktakes,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var warehouseId = await db.Stocktakes
                .AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => (Guid?)s.WarehouseId)
                .FirstOrDefaultAsync(ct);

            if (warehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.CountApprove, warehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await stocktakes.PostAsync(id, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                stocktake => Results.Ok(ToResponse(stocktake)),
                error => error.ToHttpResult());
        })
        .RequirePermission(Permissions.Inventory.CountApprove);

        // Cancellation is only reachable before Post — the same permission that lets
        // someone start and count a stocktake lets them abandon it, since nothing has
        // reached the ledger yet either way.
        group.MapPost("/stocktakes/{id:guid}/cancel", async (
            Guid id,
            CancelStocktakeRequest request,
            InventoryDbContext db,
            StocktakeService stocktakes,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var warehouseId = await db.Stocktakes
                .AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => (Guid?)s.WarehouseId)
                .FirstOrDefaultAsync(ct);

            if (warehouseId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Inventory.CountPerform, warehouseId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await stocktakes.CancelAsync(id, request.Reason, currentUser.UserId ?? Guid.Empty, ct);

            return result.Match(
                stocktake => Results.Ok(ToResponse(stocktake)),
                error => error.ToHttpResult());
        })
        .AddValidation<CancelStocktakeRequest>()
        .RequirePermission(Permissions.Inventory.CountPerform);
    }

    private static StockTransferResponse ToResponse(POS.Inventory.Domain.StockTransfer t) => new(
        t.Id,
        t.SourceWarehouseId,
        t.DestinationWarehouseId,
        t.InTransitWarehouseId,
        t.TransferNumber,
        t.Status.ToString(),
        t.HasVariance,
        [.. t.Lines.Select(l => new StockTransferLineResponse(l.VariantId, l.QuantitySent, l.QuantityReceived, l.Variance))]);

    private static StocktakeResponse ToResponse(POS.Inventory.Domain.Stocktake s) => new(
        s.Id,
        s.WarehouseId,
        s.StocktakeNumber,
        s.Scope.ToString(),
        s.IsBlind,
        s.Status.ToString(),
        [.. s.Lines.Select(l => new StocktakeLineResponse(l.VariantId, l.CountedQuantity, l.ExpectedQuantity, l.Variance))]);

    private static StockBalanceResponse ToResponse(POS.Inventory.Domain.StockBalance b) => new(
        b.WarehouseId,
        b.VariantId,
        b.QuantityOnHand,
        b.AverageUnitCost.Amount,
        b.TotalValue.Amount,
        b.AverageUnitCost.Currency,
        b.IsNegative,
        b.LastMovementAt);
}

public sealed record StockBalanceResponse(
    Guid WarehouseId,
    Guid VariantId,
    decimal QuantityOnHand,
    decimal AverageUnitCost,
    decimal TotalValue,
    string Currency,
    bool IsNegative,
    DateTimeOffset LastMovementAt);

public sealed record StockMovementResponse(
    Guid Id,
    string Type,
    decimal QuantityDelta,
    decimal UnitCost,
    string DocumentType,
    Guid DocumentId,
    string? ReasonCode,
    DateTimeOffset OccurredAt,
    DateOnly BusinessDate);

public sealed record CreateAdjustmentRequest(
    Guid WarehouseId,
    Guid VariantId,
    StockAdjustmentKind Kind,
    decimal Quantity,
    string ReasonCode,
    DateOnly BusinessDate,
    decimal? UnitCost = null,
    string? Currency = null,
    string? Notes = null);

public sealed class CreateAdjustmentRequestValidator : AbstractValidator<CreateAdjustmentRequest>
{
    public CreateAdjustmentRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.Kind).IsInEnum();

        // Positive magnitude; direction is the kind's job. A zero adjustment is a
        // no-op the ledger rejects anyway.
        RuleFor(r => r.Quantity).GreaterThan(0m);

        // Every adjustment type is reason-required in the domain. Validating here turns
        // what would be a 422 from deep in the movement factory into a 400 naming the
        // field.
        RuleFor(r => r.ReasonCode).NotEmpty().MaximumLength(50);
        RuleFor(r => r.Notes).MaximumLength(500);

        // An increase adds units at a stated cost, so the cost is mandatory and its
        // currency with it. A reduction consumes at the prevailing average and must NOT
        // carry a cost — supplying one would imply a choice the operator does not get.
        When(r => r.Kind == StockAdjustmentKind.Increase, () =>
        {
            RuleFor(r => r.UnitCost).NotNull().GreaterThan(0m);
            RuleFor(r => r.Currency).NotEmpty().Length(3);
        });

        When(r => r.Kind != StockAdjustmentKind.Increase, () =>
            RuleFor(r => r.UnitCost)
                .Null()
                .WithMessage("A reduction consumes stock at the prevailing average cost; do not supply a unit cost."));
    }
}

public sealed record StockTransferLineResponse(
    Guid VariantId,
    decimal QuantitySent,
    decimal? QuantityReceived,
    decimal Variance);

public sealed record StockTransferResponse(
    Guid Id,
    Guid SourceWarehouseId,
    Guid DestinationWarehouseId,
    Guid InTransitWarehouseId,
    string TransferNumber,
    string Status,
    bool HasVariance,
    IReadOnlyList<StockTransferLineResponse> Lines);

public sealed record CreateStockTransferLineRequest(Guid VariantId, decimal Quantity);

public sealed record CreateStockTransferRequest(
    Guid SourceWarehouseId,
    Guid DestinationWarehouseId,
    Guid InTransitWarehouseId,
    string TransferNumber,
    IReadOnlyList<CreateStockTransferLineRequest> Lines);

public sealed class CreateStockTransferRequestValidator : AbstractValidator<CreateStockTransferRequest>
{
    public CreateStockTransferRequestValidator()
    {
        RuleFor(r => r.SourceWarehouseId).NotEmpty();
        RuleFor(r => r.DestinationWarehouseId).NotEmpty();
        RuleFor(r => r.InTransitWarehouseId).NotEmpty();

        RuleFor(r => r.SourceWarehouseId)
            .NotEqual(r => r.DestinationWarehouseId)
            .WithMessage("A transfer must move stock between different warehouses.");

        RuleFor(r => r.TransferNumber).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Lines).NotEmpty();

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);
        });
    }
}

public sealed record ReceivedQuantityLine(Guid VariantId, decimal Quantity);

public sealed record ReceiveStockTransferRequest(IReadOnlyList<ReceivedQuantityLine> ReceivedQuantities);

public sealed class ReceiveStockTransferRequestValidator : AbstractValidator<ReceiveStockTransferRequest>
{
    public ReceiveStockTransferRequestValidator()
    {
        RuleFor(r => r.ReceivedQuantities).NotNull();

        RuleForEach(r => r.ReceivedQuantities).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();

            // Zero is a legitimate answer — nothing of this line arrived — and is how a
            // total loss is reported, so it is not rejected the way a manual adjustment's
            // zero quantity would be.
            line.RuleFor(l => l.Quantity).GreaterThanOrEqualTo(0m);
        });
    }
}

public sealed record WriteOffVarianceRequest(string ReasonCode);

public sealed class WriteOffVarianceRequestValidator : AbstractValidator<WriteOffVarianceRequest>
{
    public WriteOffVarianceRequestValidator() =>
        RuleFor(r => r.ReasonCode).NotEmpty().MaximumLength(200);
}

public sealed record CancelStockTransferRequest(string Reason);

public sealed class CancelStockTransferRequestValidator : AbstractValidator<CancelStockTransferRequest>
{
    public CancelStockTransferRequestValidator() =>
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(200);
}

public sealed record StocktakeLineResponse(
    Guid VariantId,
    decimal CountedQuantity,
    decimal ExpectedQuantity,
    decimal Variance);

public sealed record StocktakeResponse(
    Guid Id,
    Guid WarehouseId,
    string StocktakeNumber,
    string Scope,
    bool IsBlind,
    string Status,
    IReadOnlyList<StocktakeLineResponse> Lines);

public sealed record StartStocktakeApiRequest(
    Guid WarehouseId,
    string StocktakeNumber,
    POS.Inventory.Domain.StocktakeScope Scope,
    bool IsBlind,
    DateOnly BusinessDate);

public sealed class StartStocktakeApiRequestValidator : AbstractValidator<StartStocktakeApiRequest>
{
    public StartStocktakeApiRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.StocktakeNumber).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Scope).IsInEnum();
    }
}

public sealed record RecordCountRequest(Guid VariantId, decimal CountedQuantity);

public sealed class RecordCountRequestValidator : AbstractValidator<RecordCountRequest>
{
    public RecordCountRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.CountedQuantity).GreaterThanOrEqualTo(0m);
    }
}

public sealed record CancelStocktakeRequest(string Reason);

public sealed class CancelStocktakeRequestValidator : AbstractValidator<CancelStocktakeRequest>
{
    public CancelStocktakeRequestValidator() =>
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(200);
}
