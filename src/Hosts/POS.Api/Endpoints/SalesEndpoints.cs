using FluentValidation;
using Microsoft.EntityFrameworkCore;
using POS.Catalog.Domain;
using POS.Catalog.Persistence;
using POS.Common.Errors;
using POS.Common.Persistence;
using POS.Common.Security;
using POS.Common.Tenancy;
using POS.Common.Validation;
using POS.Contracts.Fiscal;
using POS.Contracts.Inventory;
using POS.Contracts.Payments;
using POS.Identity.Authorization;
using POS.Inventory.Persistence;
using POS.Sales.Domain;
using POS.Sales.Persistence;
using POS.Sales.Pricing;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// The till: cash shifts, ringing up a sale synchronously, holding/resuming a basket,
/// and voiding a completed sale.
/// </summary>
/// <remarks>
/// <para>
/// ASSEMBLED IN THE HOST, for the same reason <c>ReconciliationEndpoints</c> is: a
/// checkout needs a price and tax rate from Catalog, a cost from Inventory, and then
/// has to drive the Sales aggregate through the ports Purchasing's posting services
/// already establish the pattern for (<see cref="IStockPostingPort"/>,
/// <see cref="IFiscalisationPort"/>, <see cref="IPaymentRecordingPort"/>). No single
/// module owns all of that, and none of Sales/Catalog/Inventory may reference each
/// other directly (architecture rule 2) — the composition root is the one place
/// allowed to see all of it.
/// </para>
/// <para>
/// THIS IS THE ONLINE PATH, additional to — not a replacement for — the offline
/// upload at <c>POST /sync/batches</c> (<see cref="POS.Sales.Sync.SaleSyncHandler"/>).
/// Both converge on the same aggregate methods (<see cref="Sale.Open"/> /
/// <see cref="Sale.AddLine"/> / <see cref="Sale.ApplyPricing"/> /
/// <see cref="Sale.AddTender"/> / <see cref="Sale.Complete"/>), so every invariant the
/// domain enforces is enforced on both paths identically.
/// </para>
/// <para>
/// KNOWN SIMPLIFICATIONS, deliberate for this pass and worth closing before this is
/// the ONLY way a sale is created:
/// </para>
/// <list type="bullet">
///   <item>Price resolution uses <see cref="ProductVariant.DefaultPrice"/> only — no
///   <see cref="PriceList"/> resolution (branch/customer-group/effective-date
///   priority). No caller in this codebase resolves a price list yet; this is the
///   first, and it is the narrow version.</item>
///   <item>Discounts are wired through as far as a MANUAL per-line amount and a
///   whole-basket percentage — both routed straight into the existing
///   <see cref="PricingPipeline"/> stages (<c>LineDiscountStage</c>,
///   <c>OrderDiscountStage</c>). Data-driven <em>promotions</em>
///   (<c>PromotionDefinition</c>) are not wired up: nothing persists a promotion
///   definition anywhere yet, so there is nothing for this endpoint to load. That is a
///   separate feature (storage + an admin screen to define them), not a pricing gap.</item>
///   <item>Receipt numbering is "max sequence plus one, guarded by the unique index,
///   retry on conflict" rather than a dedicated gap-free allocator. Honest under
///   concurrent checkouts on the SAME terminal (rare — one cashier, one till) and
///   safe (the index is the real guarantee, exactly as <c>SaleConfiguration</c>'s own
///   remarks describe), but a dedicated allocator would avoid the retry entirely.</item>
///   <item>No <c>Terminal</c>/<c>Warehouse</c> existence check — both are taken as
///   caller-supplied ids, same trust level <see cref="Shift"/> and <see cref="Sale"/>
///   already give <c>TerminalId</c> in their own factories.</item>
///   <item><b>Void has no fiscal or electronic-payment reversal.</b> Stock is reversed
///   (a real <see cref="StockPostingKind.CustomerReturn"/> movement) and the sale
///   flips to <see cref="SaleStatus.Voided"/>, but neither
///   <see cref="IFiscalisationPort"/> nor <see cref="IPaymentRecordingPort"/> exposes a
///   credit-note/refund method today — extending both ports is a separate, larger
///   change. The void reason is logged, not persisted: <see cref="Sale.MarkVoided"/>
///   takes no reason parameter, and adding one is a domain change, not an endpoint one.</item>
///   <item><b>Held sales are frozen, not editable.</b> <c>POST /sales/hold</c> prices
///   and parks a basket; <c>POST /sales/{id}/resume</c> reopens it for payment, but
///   there is no way to add/remove lines on a resumed sale before completing it — that
///   would need a further <c>POST /sales/{id}/lines</c> endpoint, not built here.</item>
/// </list>
/// </remarks>
public static class SalesEndpoints
{
    private const string CashCurrencyDefaultTaxCode = "STD";

    public static IEndpointRouteBuilder MapSalesRegisterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/sales").RequireAuthorization();

        // ---------------------------------------------------------------
        // Shifts
        // ---------------------------------------------------------------

        group.MapGet("/shifts/current", async (
            Guid terminalId,
            ITenantContext tenant,
            SalesDbContext db,
            CancellationToken ct) =>
        {
            var shift = await db.Shifts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenant.TenantId && s.TerminalId == terminalId && s.Status == ShiftStatus.Open, ct);

            return shift is null ? Results.NotFound() : Results.Ok(ShiftResponse.From(shift));
        });

        group.MapPost("/shifts", async (
            OpenShiftRequest request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            SalesDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var alreadyOpen = await db.Shifts.AsNoTracking().AnyAsync(
                s => s.TenantId == tenant.TenantId && s.TerminalId == request.TerminalId && s.Status == ShiftStatus.Open,
                ct);

            if (alreadyOpen)
                return SalesEndpointErrors.ShiftAlreadyOpenForTerminal.ToHttpResult();

            var now = clock.UtcNow;

            var shift = Shift.Open(
                tenant.TenantId,
                request.BranchId,
                request.TerminalId,
                currentUser.UserId ?? Guid.Empty,
                new Money(request.OpeningFloat, request.Currency),
                DateOnly.FromDateTime(now.UtcDateTime),
                now);

            db.Shifts.Add(shift);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/sales/shifts/{shift.Id}", ShiftResponse.From(shift));
        })
        .AddValidation<OpenShiftRequest>()
        .RequirePermission(Permissions.Cash.OpenShift);

        group.MapPost("/shifts/{id:guid}/close", async (
            Guid id,
            CloseShiftRequest request,
            ITenantContext tenant,
            SalesDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);
            if (shift is null)
                return Results.NotFound();

            // Cash sales for this shift, so the blind-close expected figure includes
            // what was actually taken at the till. Tender has no DbSet of its own (see
            // SalesDbContext's remarks) — it is a dependent of Sale, so it is reached
            // through it.
            var shiftSales = await db.Sales.AsNoTracking()
                .Include(s => s.Tenders)
                .Where(s => s.ShiftId == id && s.Status == SaleStatus.Completed)
                .ToListAsync(ct);

            // Net cash retained, not gross cash tendered: a customer handing over a
            // $30 note against a $23.55 total tenders $30 in cash but leaves only
            // $23.55 in the drawer — the other $6.45 goes back out as change. Only
            // cash can produce change (Sale.AddTender's overtender rule), so
            // subtracting each sale's ChangeGiven from its cash tenders is exact, not
            // an approximation. Filtering to Completed excludes Voided sales — their
            // cash went back to the customer, and Cancelled sales never took any.
            var cashSales = shiftSales.Sum(s =>
                s.Tenders.Where(t => t.Method == TenderMethod.Cash).Sum(t => t.Amount.Amount)
                - s.ChangeGiven.Amount);

            var currency = shift.Currency;

            var result = shift.Close(
                new Money(request.CountedCash, currency),
                new Money(cashSales, currency),
                Money.Zero(currency),
                clock.UtcNow);

            if (result.IsFailure)
                return result.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);

            return Results.Ok(ShiftResponse.From(shift));
        })
        .AddValidation<CloseShiftRequest>()
        .RequirePermission(Permissions.Cash.CloseShift);

        // ---------------------------------------------------------------
        // Register — products priced and taxed for the checkout screen
        // ---------------------------------------------------------------

        group.MapGet("/register-products", async (
            CatalogDbContext catalog,
            IClock clock,
            CancellationToken ct) =>
        {
            var variants = await catalog.Variants
                .AsNoTracking()
                .Where(v => v.IsActive)
                .Select(v => new
                {
                    v.Id,
                    v.ProductId,
                    v.Sku,
                    v.Name,
                    v.DefaultPriceAmount,
                    v.DefaultPriceCurrency,
                })
                .ToListAsync(ct);

            var productIds = variants.Select(v => v.ProductId).Distinct().ToList();

            var products = await catalog.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.TaxGroupId, p.UnitOfMeasureId, p.CategoryId })
                .ToListAsync(ct);

            var taxGroupIds = products.Select(p => p.TaxGroupId).Distinct().ToList();
            var taxGroups = await catalog.TaxGroups
                .AsNoTracking()
                .Where(t => taxGroupIds.Contains(t.Id))
                .Include(t => t.Rates)
                .ToListAsync(ct);

            var uomIds = products.Select(p => p.UnitOfMeasureId).Distinct().ToList();
            var uoms = await catalog.UnitsOfMeasure
                .AsNoTracking()
                .Where(u => uomIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Code, ct);

            var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
            var categories = await catalog.Categories
                .AsNoTracking()
                .Where(c => categoryIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            var productById = products.ToDictionary(p => p.Id);
            var taxGroupById = taxGroups.ToDictionary(t => t.Id);
            var now = clock.UtcNow;

            var items = variants.Select(v =>
            {
                var product = productById.GetValueOrDefault(v.ProductId);
                var taxGroup = product is not null ? taxGroupById.GetValueOrDefault(product.TaxGroupId) : null;

                // No rate ever configured (a variant created through the simple
                // `POST /catalog/products` path — see its remarks) means tax-free,
                // not an error: the register still has to be able to sell the item.
                var rate = taxGroup?.RateAt(now);

                return new RegisterProductResponse(
                    v.Id,
                    v.ProductId,
                    v.Sku,
                    v.Name,
                    v.DefaultPriceAmount,
                    v.DefaultPriceCurrency,
                    product?.CategoryId ?? Guid.Empty,
                    product is not null ? categories.GetValueOrDefault(product.CategoryId, "—") : "—",
                    rate?.Percentage / 100m ?? 0m,
                    rate?.IsInclusive ?? false,
                    CashCurrencyDefaultTaxCode,
                    product is not null ? uoms.GetValueOrDefault(product.UnitOfMeasureId, "EA") : "EA");
            }).OrderBy(i => i.Name).ToList();

            return Results.Ok(new RegisterProductListResponse(items));
        })
        .RequirePermission(Permissions.Catalog.ProductView);

        // ---------------------------------------------------------------
        // Sales
        // ---------------------------------------------------------------

        group.MapGet("/", async (
            ITenantContext tenant,
            SalesDbContext db,
            CancellationToken ct) =>
        {
            var sales = await db.Sales.AsNoTracking()
                .Where(s => s.TenantId == tenant.TenantId)
                .OrderByDescending(s => s.OpenedAt)
                .Take(50)
                .ToListAsync(ct);

            var items = sales.Select(SaleSummaryResponse.From).ToList();

            return Results.Ok(new SaleListResponse(items));
        });

        group.MapGet("/held", async (
            Guid shiftId,
            ITenantContext tenant,
            SalesDbContext db,
            CancellationToken ct) =>
        {
            var sales = await db.Sales.AsNoTracking()
                .Where(s => s.TenantId == tenant.TenantId && s.ShiftId == shiftId && s.Status == SaleStatus.Suspended)
                .OrderBy(s => s.OpenedAt)
                .ToListAsync(ct);

            var items = sales.Select(SaleSummaryResponse.From).ToList();

            return Results.Ok(new SaleListResponse(items));
        })
        .RequirePermission(Permissions.Sales.Suspend);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITenantContext tenant,
            SalesDbContext db,
            CancellationToken ct) =>
        {
            var sale = await db.Sales.AsNoTracking()
                .Include(s => s.Lines)
                .Include(s => s.Tenders)
                .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);

            return sale is null ? Results.NotFound() : Results.Ok(SaleDetailResponse.From(sale));
        });

        group.MapPost("/", async (
            CreateSaleRequest request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            SalesDbContext salesDb,
            CatalogDbContext catalogDb,
            InventoryDbContext inventoryDb,
            PricingPipeline pricing,
            IStockPostingPort stock,
            IFiscalisationPort fiscal,
            IPaymentRecordingPort payments,
            IClock clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("POS.Api.Endpoints.SalesEndpoints");
            var now = clock.UtcNow;

            var shift = await salesDb.Shifts.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == request.ShiftId && s.TenantId == tenant.TenantId, ct);

            if (shift is null)
                return SalesEndpointErrors.ShiftNotFound.ToHttpResult();

            if (shift.Status != ShiftStatus.Open)
                return ShiftErrors.ShiftNotOpen(shift.Status).ToHttpResult();

            var built = await BuildPricedSaleAsync(
                request.Lines, request.OrderDiscountPercent, request.Currency,
                catalogDb, inventoryDb, request.WarehouseId, pricing, clock, ct);

            if (built.IsFailure)
                return built.Error.ToHttpResult();

            var receiptNumber = await AllocateReceiptNumberAsync(salesDb, tenant.TenantId, request.TerminalId, ct);

            var sale = Sale.Open(
                tenant.TenantId,
                request.CompanyId,
                request.BranchId,
                request.TerminalId,
                request.ShiftId,
                currentUser.UserId ?? Guid.Empty,
                request.Currency,
                receiptNumber,
                shift.BusinessDate,
                now);

            var addedLines = AddPricedLines(sale, built.Value);
            if (addedLines.IsFailure)
                return addedLines.Error.ToHttpResult();

            var tendered = ApplyTendersAndComplete(sale, request.Tenders, now);
            if (tendered.IsFailure)
                return tendered.Error.ToHttpResult();

            // Stock moves before the sale is saved — same ordering as
            // GoodsReceiptPostingService and SaleSyncHandler, and for the same reason:
            // a crash here should leave stock moved and no sale (self-healing on
            // retry), never a visible sale whose stock never left the shelf.
            var stockResult = await PostSaleStockAsync(stock, sale, request.WarehouseId, currentUser, ct);
            if (stockResult.IsFailure)
                return stockResult.Error.ToHttpResult();

            salesDb.Sales.Add(sale);

            try
            {
                await salesDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                // Two checkouts on the same terminal raced for the same receipt
                // number. Stock has already moved for THIS sale id and nothing else
                // has — safe to ask the caller to retry the checkout, which allocates
                // a fresh number.
                salesDb.ChangeTracker.Clear();
                return SalesEndpointErrors.ReceiptNumberConflict.ToHttpResult();
            }

            await FiscaliseAsync(fiscal, sale, now, logger, ct);
            await RecordPaymentsAsync(payments, sale, shift.BusinessDate, logger, ct);

            return Results.Created($"/api/v1/sales/{sale.Id}", SaleDetailResponse.From(sale));
        })
        .AddValidation<CreateSaleRequest>()
        .RequirePermission(Permissions.Sales.Create);

        // ---------------------------------------------------------------
        // Hold / resume — park a priced basket, come back to it later
        // ---------------------------------------------------------------

        group.MapPost("/hold", async (
            HoldSaleRequest request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            SalesDbContext salesDb,
            CatalogDbContext catalogDb,
            InventoryDbContext inventoryDb,
            PricingPipeline pricing,
            IClock clock,
            CancellationToken ct) =>
        {
            var now = clock.UtcNow;

            var shift = await salesDb.Shifts.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == request.ShiftId && s.TenantId == tenant.TenantId, ct);

            if (shift is null)
                return SalesEndpointErrors.ShiftNotFound.ToHttpResult();

            if (shift.Status != ShiftStatus.Open)
                return ShiftErrors.ShiftNotOpen(shift.Status).ToHttpResult();

            var built = await BuildPricedSaleAsync(
                request.Lines, request.OrderDiscountPercent, request.Currency,
                catalogDb, inventoryDb, request.WarehouseId, pricing, clock, ct);

            if (built.IsFailure)
                return built.Error.ToHttpResult();

            var receiptNumber = await AllocateReceiptNumberAsync(salesDb, tenant.TenantId, request.TerminalId, ct);

            var sale = Sale.Open(
                tenant.TenantId,
                request.CompanyId,
                request.BranchId,
                request.TerminalId,
                request.ShiftId,
                currentUser.UserId ?? Guid.Empty,
                request.Currency,
                receiptNumber,
                shift.BusinessDate,
                now);

            var addedLines = AddPricedLines(sale, built.Value);
            if (addedLines.IsFailure)
                return addedLines.Error.ToHttpResult();

            // No tenders yet — Suspend requires an empty tender list (a sale that has
            // already taken payment cannot be parked, per Sale.Suspend's own remarks).
            var suspended = sale.Suspend(now);
            if (suspended.IsFailure)
                return suspended.Error.ToHttpResult();

            salesDb.Sales.Add(sale);

            try
            {
                await salesDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                salesDb.ChangeTracker.Clear();
                return SalesEndpointErrors.ReceiptNumberConflict.ToHttpResult();
            }

            return Results.Created($"/api/v1/sales/{sale.Id}", SaleDetailResponse.From(sale));
        })
        .AddValidation<HoldSaleRequest>()
        .RequirePermission(Permissions.Sales.Suspend);

        group.MapPost("/{id:guid}/resume", async (
            Guid id,
            ResumeSaleRequest request,
            ITenantContext tenant,
            SalesDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var sale = await db.Sales
                .Include(s => s.Lines)
                .Include(s => s.Tenders)
                .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);

            if (sale is null)
                return Results.NotFound();

            var resumed = sale.Resume(request.TerminalId, clock.UtcNow);
            if (resumed.IsFailure)
                return resumed.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);

            return Results.Ok(SaleDetailResponse.From(sale));
        })
        .AddValidation<ResumeSaleRequest>()
        .RequirePermission(Permissions.Sales.Suspend);

        group.MapPost("/{id:guid}/complete-held", async (
            Guid id,
            CompleteHeldSaleRequest request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            SalesDbContext salesDb,
            IStockPostingPort stock,
            IFiscalisationPort fiscal,
            IPaymentRecordingPort payments,
            IClock clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("POS.Api.Endpoints.SalesEndpoints");
            var now = clock.UtcNow;

            var sale = await salesDb.Sales
                .Include(s => s.Lines)
                .Include(s => s.Tenders)
                .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);

            if (sale is null)
                return Results.NotFound();

            var tendered = ApplyTendersAndComplete(sale, request.Tenders, now);
            if (tendered.IsFailure)
                return tendered.Error.ToHttpResult();

            // `sale` was loaded from the database (this is the resume/complete path,
            // not fresh checkout), so it is already tracked. EF's cascade-tracking
            // heuristic for a newly-discovered child of an ALREADY-TRACKED parent
            // decides Added-vs-Modified from whether the child's key looks
            // "default" — and Tender.Id is a client-generated, non-default Guid the
            // moment Tender.Create() runs, so every Tender AddTender() just added
            // here gets misread as an existing row and marked Modified, which then
            // fails as a 0-row concurrency conflict (there is no such row to update).
            // Telling EF explicitly is the fix; the same code on the fresh-checkout
            // path never hits this because the ROOT Sale being Added cascades Added
            // to everything under it, key value notwithstanding.
            foreach (var tender in sale.Tenders)
            {
                if (salesDb.Entry(tender).State != EntityState.Added)
                    salesDb.Entry(tender).State = EntityState.Added;
            }

            var stockResult = await PostSaleStockAsync(stock, sale, request.WarehouseId, currentUser, ct);
            if (stockResult.IsFailure)
                return stockResult.Error.ToHttpResult();

            await salesDb.SaveChangesAsync(ct);

            await FiscaliseAsync(fiscal, sale, now, logger, ct);
            await RecordPaymentsAsync(payments, sale, sale.BusinessDate, logger, ct);

            return Results.Ok(SaleDetailResponse.From(sale));
        })
        .AddValidation<CompleteHeldSaleRequest>()
        .RequirePermission(Permissions.Sales.Create);

        // ---------------------------------------------------------------
        // Void — reverse a completed sale
        // ---------------------------------------------------------------

        group.MapPost("/{id:guid}/void", async (
            Guid id,
            VoidSaleRequest request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            SalesDbContext db,
            IStockPostingPort stock,
            IClock clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("POS.Api.Endpoints.SalesEndpoints");

            var sale = await db.Sales
                .Include(s => s.Lines)
                .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);

            if (sale is null)
                return Results.NotFound();

            var voided = sale.MarkVoided();
            if (voided.IsFailure)
                return voided.Error.ToHttpResult();

            var now = clock.UtcNow;

            // Deterministic, not random: a retried request (network blip between the
            // stock post below and this row's save) must reverse the SAME document
            // twice-called, not create a second reversal. The adapter's idempotency
            // check keys purely on DocumentId (see StockPostingAdapter's remarks), so
            // reusing sale.Id verbatim would collide with the ORIGINAL sale posting and
            // silently no-op instead of reversing it — flipping every bit gives a
            // distinct id that is still the same value on every retry.
            var reversalDocumentId = DeriveVoidDocumentId(sale.Id);

            var stockResult = await stock.PostAsync(new StockPostingRequest
            {
                WarehouseId = request.WarehouseId,
                Kind = StockPostingKind.CustomerReturn,
                DocumentId = reversalDocumentId,
                DocumentNumber = $"VOID-{sale.ReceiptNumber}",
                OccurredAt = now,
                BusinessDate = sale.BusinessDate,
                UserId = currentUser.UserId,
                Lines = [.. sale.Lines.Select(l => new StockPostingLine(l.VariantId, l.Quantity, l.UnitCostAtSale))],
            }, ct);

            if (stockResult.IsFailure)
                return stockResult.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);

            // Not persisted — see this file's remarks on why a void reason has nowhere
            // to live on Sale yet. Logged so it isn't simply lost.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Sale {ReceiptNumber} ({SaleId}) voided by user {UserId}: {Reason}",
                    sale.ReceiptNumber.ToString(), sale.Id, currentUser.UserId, request.Reason);
            }

            return Results.Ok(SaleDetailResponse.From(sale));
        })
        .AddValidation<VoidSaleRequest>()
        .RequirePermission(Permissions.Sales.Void);

        return app;
    }

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------

    private sealed record PricedSale(
        IReadOnlyList<(Guid VariantId, string Description, string UnitOfMeasure, decimal Quantity, Money UnitPrice, string TaxCode, decimal TaxRate, bool TaxInclusive, Money UnitCost)> Lines,
        IReadOnlyList<LinePricing> Pricing,
        Money RoundingAdjustment);

    /// <summary>
    /// Resolves price/tax/cost for every requested line and runs the pricing
    /// pipeline. Shared by the immediate checkout and the hold endpoint — both build
    /// an identically-priced set of lines, they just differ in what happens next
    /// (complete now vs. suspend for later).
    /// </summary>
    private static async Task<Result<PricedSale>> BuildPricedSaleAsync(
        IReadOnlyList<CreateSaleLineRequest> requestedLines,
        decimal? orderDiscountPercent,
        string currency,
        CatalogDbContext catalogDb,
        InventoryDbContext inventoryDb,
        Guid warehouseId,
        PricingPipeline pricing,
        IClock clock,
        CancellationToken ct)
    {
        var variantIds = requestedLines.Select(l => l.VariantId).Distinct().ToList();

        var variants = await catalogDb.Variants.AsNoTracking()
            .Where(v => variantIds.Contains(v.Id))
            .ToListAsync(ct);

        if (variants.Count != variantIds.Count)
            return Result<PricedSale>.Failure(SalesEndpointErrors.UnknownVariant);

        var productIds = variants.Select(v => v.ProductId).Distinct().ToList();
        var products = await catalogDb.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(ct);

        var taxGroupIds = products.Select(p => p.TaxGroupId).Distinct().ToList();
        var taxGroups = await catalogDb.TaxGroups.AsNoTracking()
            .Include(t => t.Rates)
            .Where(t => taxGroupIds.Contains(t.Id))
            .ToListAsync(ct);

        var uomIds = products.Select(p => p.UnitOfMeasureId).Distinct().ToList();
        var uoms = await catalogDb.UnitsOfMeasure.AsNoTracking()
            .Where(u => uomIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Code, ct);

        var balances = await inventoryDb.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId && variantIds.Contains(b.VariantId))
            .ToDictionaryAsync(b => b.VariantId, ct);

        var productById = products.ToDictionary(p => p.Id);
        var taxGroupById = taxGroups.ToDictionary(t => t.Id);
        var now = clock.UtcNow;

        var lineInputs = new List<PricingLineInput>();
        var lines = new List<(Guid, string, string, decimal, Money, string, decimal, bool, Money)>();

        for (var i = 0; i < requestedLines.Count; i++)
        {
            var requested = requestedLines[i];
            var variant = variants.First(v => v.Id == requested.VariantId);
            var product = productById.GetValueOrDefault(variant.ProductId);
            var taxGroup = product is not null ? taxGroupById.GetValueOrDefault(product.TaxGroupId) : null;
            var rate = taxGroup?.RateAt(now);

            var unitPrice = new Money(variant.DefaultPriceAmount, variant.DefaultPriceCurrency);
            if (unitPrice.Currency != currency)
            {
                return Result<PricedSale>.Failure(
                    SalesEndpointErrors.PriceCurrencyMismatch(variant.Sku, unitPrice.Currency, currency));
            }

            var unitCost = balances.TryGetValue(requested.VariantId, out var balance)
                ? balance.AverageUnitCost
                : Money.Zero(currency);

            var taxRate = rate?.Percentage / 100m ?? 0m;
            var taxInclusive = rate?.IsInclusive ?? false;
            var uom = product is not null ? uoms.GetValueOrDefault(product.UnitOfMeasureId, "EA") : "EA";

            lineInputs.Add(new PricingLineInput(
                i + 1,
                variant.Id,
                variant.Name,
                requested.Quantity,
                unitPrice,
                CashCurrencyDefaultTaxCode,
                taxRate,
                taxInclusive,
                ManualDiscount: requested.DiscountAmount is { } d and > 0m ? new Money(d, currency) : null));

            lines.Add((variant.Id, variant.Name, uom, requested.Quantity, unitPrice, CashCurrencyDefaultTaxCode, taxRate, taxInclusive, unitCost));
        }

        var orderDiscount = orderDiscountPercent is { } pct and > 0m
            ? new OrderDiscount(OrderDiscountKind.Percentage, pct, "Order discount")
            : null;

        var priced = pricing.Price(new PricingContext
        {
            Currency = currency,
            Lines = lineInputs,
            TaxRounding = TaxRoundingRule.PerLine,
            CashRounding = CashRoundingRule.None,
            OrderDiscount = orderDiscount,
        });

        if (priced.IsFailure)
            return Result<PricedSale>.Failure(priced.Error);

        return Result<PricedSale>.Success(new PricedSale(
            lines,
            [.. priced.Value.Lines.Select(l => new LinePricing(l.LineNumber, l.Discount, l.Net, l.Tax, l.Gross))],
            priced.Value.RoundingAdjustment));
    }

    private static Result AddPricedLines(Sale sale, PricedSale priced)
    {
        foreach (var line in priced.Lines)
        {
            var added = sale.AddLine(SaleLine.Create(
                line.VariantId, line.Description, line.Quantity, line.UnitOfMeasure,
                line.UnitPrice, line.TaxCode, line.TaxRate, line.TaxInclusive, line.UnitCost,
                priceListId: null, priceListVersion: null));

            if (added.IsFailure)
                return added;
        }

        return sale.ApplyPricing(priced.Pricing, priced.RoundingAdjustment);
    }

    private static Result ApplyTendersAndComplete(Sale sale, IReadOnlyList<CreateSaleTenderRequest> tenders, DateTimeOffset now)
    {
        foreach (var tender in tenders)
        {
            if (!Enum.TryParse<TenderMethod>(tender.Method, ignoreCase: true, out var method))
                return Result.Failure(SalesEndpointErrors.UnknownTenderMethod(tender.Method));

            var added = sale.AddTender(
                Tender.Create(method, new Money(tender.Amount, sale.Currency), now, tender.Reference),

                // A checkout completing synchronously through this host is, by
                // definition, online right now — unlike the sync path, which is
                // replaying what a terminal already decided while disconnected.
                terminalIsOnline: true);

            if (added.IsFailure)
                return added;
        }

        return sale.Complete(now);
    }

    private static async Task<Result> PostSaleStockAsync(
        IStockPostingPort stock, Sale sale, Guid warehouseId, ICurrentUser currentUser, CancellationToken ct)
    {
        var result = await stock.PostAsync(new StockPostingRequest
        {
            WarehouseId = warehouseId,
            Kind = StockPostingKind.Sale,
            DocumentId = sale.Id,
            DocumentNumber = sale.ReceiptNumber.ToString(),
            OccurredAt = sale.CompletedAt ?? sale.OpenedAt,
            BusinessDate = sale.BusinessDate,
            UserId = currentUser.UserId,
            Lines = [.. sale.Lines.Select(l => new StockPostingLine(l.VariantId, l.Quantity, l.UnitCostAtSale))],
        }, ct);

        return result;
    }

    /// <summary>See this file's remarks on void — flips every bit so the id is stable across retries but never collides with the original sale's own document id.</summary>
    private static Guid DeriveVoidDocumentId(Guid saleId)
    {
        Span<byte> bytes = stackalloc byte[16];
        saleId.TryWriteBytes(bytes);
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)~bytes[i];

        return new Guid(bytes);
    }

    /// <summary>
    /// Next receipt number for a terminal: highest existing sequence plus one, in the
    /// terminal's own series. Guarded by the unique index at save time (see
    /// <c>SaleConfiguration</c>'s remarks) — this is the mechanism, not the guarantee.
    /// </summary>
    private static async Task<ReceiptNumber> AllocateReceiptNumberAsync(
        SalesDbContext db, Guid tenantId, Guid terminalId, CancellationToken ct)
    {
        const string series = "POS";

        var last = await db.Sales.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.TerminalId == terminalId && s.ReceiptNumber.Series == series)
            .Select(s => (long?)s.ReceiptNumber.Sequence)
            .MaxAsync(ct);

        return new ReceiptNumber(series, (last ?? 0) + 1);
    }

    private static async Task FiscaliseAsync(IFiscalisationPort fiscal, Sale sale, DateTimeOffset issuedAt, ILogger logger, CancellationToken ct)
    {
        var result = await fiscal.FiscaliseSaleAsync(new FiscaliseSaleRequest
        {
            CompanyId = sale.CompanyId,
            BranchId = sale.BranchId,
            TerminalId = sale.TerminalId,
            SaleId = sale.Id,
            Currency = sale.Currency,
            IssuedAt = issuedAt,
            BusinessDate = sale.BusinessDate,
            TotalExclusiveTax = sale.TotalExclusiveTax.Amount,
            TotalTax = sale.TotalTax.Amount,
            TotalInclusiveTax = sale.TotalInclusiveTax.Amount,
            IssuedOffline = false,
            Lines = [.. sale.Lines.Select(l => new FiscaliseSaleLine(
                l.LineNumber, l.Description, l.Quantity, l.UnitOfMeasure,
                l.UnitPrice.Amount, l.DiscountAmount.Amount, l.TaxCode, l.TaxRate,
                l.TaxAmount.Amount, l.GrossAmount.Amount))],
        }, ct);

        if (result.IsFailure)
        {
            logger.LogError(
                "Sale {ReceiptNumber} ({SaleId}) was recorded but could not be fiscalised: {Code} {Message}",
                sale.ReceiptNumber.ToString(), sale.Id, result.Error.Code, result.Error.Message);
        }
    }

    private static async Task RecordPaymentsAsync(IPaymentRecordingPort payments, Sale sale, DateOnly businessDate, ILogger logger, CancellationToken ct)
    {
        var electronic = sale.Tenders
            .Select((t, index) => (t, index))
            .Where(x => x.t.Method != TenderMethod.Cash)
            .Select(x => new RecordedTender(x.index, x.t.Method.ToString(), x.t.Amount, x.t.TakenAt, x.t.Reference))
            .ToList();

        if (electronic.Count == 0)
            return;

        var result = await payments.RecordSaleTendersAsync(new RecordSaleTendersRequest
        {
            BranchId = sale.BranchId,
            TerminalId = sale.TerminalId,
            SaleId = sale.Id,
            BusinessDate = businessDate,
            Tenders = electronic,
        }, ct);

        if (result.IsFailure)
        {
            logger.LogError(
                "Sale {ReceiptNumber} ({SaleId}) was recorded but its tenders could not be recorded as payments: {Code} {Message}",
                sale.ReceiptNumber.ToString(), sale.Id, result.Error.Code, result.Error.Message);
        }
    }
}

// -----------------------------------------------------------------------
// Requests
// -----------------------------------------------------------------------

public sealed record OpenShiftRequest(Guid BranchId, Guid TerminalId, decimal OpeningFloat, string Currency);

public sealed class OpenShiftRequestValidator : AbstractValidator<OpenShiftRequest>
{
    public OpenShiftRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.TerminalId).NotEmpty();
        RuleFor(r => r.OpeningFloat).GreaterThanOrEqualTo(0m);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
    }
}

public sealed record CloseShiftRequest(decimal CountedCash);

public sealed class CloseShiftRequestValidator : AbstractValidator<CloseShiftRequest>
{
    public CloseShiftRequestValidator() => RuleFor(r => r.CountedCash).GreaterThanOrEqualTo(0m);
}

public sealed record CreateSaleLineRequest(Guid VariantId, decimal Quantity, decimal? DiscountAmount = null);

public sealed record CreateSaleTenderRequest(string Method, decimal Amount, string? Reference = null);

public sealed record CreateSaleRequest(
    Guid CompanyId,
    Guid BranchId,
    Guid TerminalId,
    Guid ShiftId,
    Guid WarehouseId,
    string Currency,
    IReadOnlyList<CreateSaleLineRequest> Lines,
    IReadOnlyList<CreateSaleTenderRequest> Tenders,
    decimal? OrderDiscountPercent = null);

public sealed class CreateSaleRequestValidator : AbstractValidator<CreateSaleRequest>
{
    public CreateSaleRequestValidator()
    {
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.TerminalId).NotEmpty();
        RuleFor(r => r.ShiftId).NotEmpty();
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Lines).NotEmpty();
        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);
            line.RuleFor(l => l.DiscountAmount).GreaterThanOrEqualTo(0m).When(l => l.DiscountAmount is not null);
        });
        RuleFor(r => r.Tenders).NotEmpty();
        RuleForEach(r => r.Tenders).ChildRules(tender =>
        {
            tender.RuleFor(t => t.Method).NotEmpty();
            tender.RuleFor(t => t.Amount).GreaterThan(0m);
        });
        RuleFor(r => r.OrderDiscountPercent).InclusiveBetween(0m, 100m).When(r => r.OrderDiscountPercent is not null);
    }
}

public sealed record HoldSaleRequest(
    Guid CompanyId,
    Guid BranchId,
    Guid TerminalId,
    Guid ShiftId,
    Guid WarehouseId,
    string Currency,
    IReadOnlyList<CreateSaleLineRequest> Lines,
    decimal? OrderDiscountPercent = null);

public sealed class HoldSaleRequestValidator : AbstractValidator<HoldSaleRequest>
{
    public HoldSaleRequestValidator()
    {
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.TerminalId).NotEmpty();
        RuleFor(r => r.ShiftId).NotEmpty();
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Lines).NotEmpty();
        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);
        });
        RuleFor(r => r.OrderDiscountPercent).InclusiveBetween(0m, 100m).When(r => r.OrderDiscountPercent is not null);
    }
}

public sealed record ResumeSaleRequest(Guid TerminalId);

public sealed class ResumeSaleRequestValidator : AbstractValidator<ResumeSaleRequest>
{
    public ResumeSaleRequestValidator() => RuleFor(r => r.TerminalId).NotEmpty();
}

public sealed record CompleteHeldSaleRequest(Guid WarehouseId, IReadOnlyList<CreateSaleTenderRequest> Tenders);

public sealed class CompleteHeldSaleRequestValidator : AbstractValidator<CompleteHeldSaleRequest>
{
    public CompleteHeldSaleRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Tenders).NotEmpty();
        RuleForEach(r => r.Tenders).ChildRules(tender =>
        {
            tender.RuleFor(t => t.Method).NotEmpty();
            tender.RuleFor(t => t.Amount).GreaterThan(0m);
        });
    }
}

public sealed record VoidSaleRequest(Guid WarehouseId, string Reason);

public sealed class VoidSaleRequestValidator : AbstractValidator<VoidSaleRequest>
{
    public VoidSaleRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(500);
    }
}

// -----------------------------------------------------------------------
// Responses
// -----------------------------------------------------------------------

public sealed record ShiftResponse(
    Guid Id, Guid BranchId, Guid TerminalId, Guid CashierId,
    decimal OpeningFloat, string Currency, DateOnly BusinessDate,
    string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    decimal? CountedCash, decimal? ExpectedCash, decimal? Variance)
{
    public static ShiftResponse From(Shift s) => new(
        s.Id, s.BranchId, s.TerminalId, s.CashierId,
        s.OpeningFloat.Amount, s.Currency, s.BusinessDate,
        s.Status.ToString(), s.OpenedAt, s.ClosedAt,
        s.Status == ShiftStatus.Closed ? s.CountedCash.Amount : null,
        s.Status == ShiftStatus.Closed ? s.ExpectedCash.Amount : null,
        s.Status == ShiftStatus.Closed ? s.Variance.Amount : null);
}

public sealed record RegisterProductResponse(
    Guid VariantId, Guid ProductId, string Sku, string Name,
    decimal Price, string Currency, Guid CategoryId, string CategoryName,
    decimal TaxRate, bool TaxInclusive, string TaxCode, string UnitOfMeasure);

public sealed record RegisterProductListResponse(IReadOnlyList<RegisterProductResponse> Items);

public sealed record SaleSummaryResponse(
    Guid Id, string ReceiptNumber, DateOnly BusinessDate, DateTimeOffset? CompletedAt,
    string Status, decimal TotalInclusiveTax, string Currency)
{
    public static SaleSummaryResponse From(Sale s) => new(
        s.Id, $"{s.ReceiptNumber.Series}-{s.ReceiptNumber.Sequence}", s.BusinessDate, s.CompletedAt,
        s.Status.ToString(), s.TotalInclusiveTax.Amount, s.Currency);
}

public sealed record SaleListResponse(IReadOnlyList<SaleSummaryResponse> Items);

public sealed record SaleLineDetailResponse(
    int LineNumber, Guid VariantId, string Description, decimal Quantity, string UnitOfMeasure,
    decimal UnitPrice, decimal Discount, decimal Net, decimal Tax, decimal Gross);

public sealed record SaleTenderDetailResponse(string Method, decimal Amount, string? Reference);

public sealed record SaleDetailResponse(
    Guid Id, string ReceiptNumber, DateOnly BusinessDate, DateTimeOffset? CompletedAt, string Status,
    string Currency, decimal TotalExclusiveTax, decimal TotalTax, decimal TotalInclusiveTax,
    decimal AmountTendered, decimal ChangeGiven,
    IReadOnlyList<SaleLineDetailResponse> Lines, IReadOnlyList<SaleTenderDetailResponse> Tenders)
{
    public static SaleDetailResponse From(Sale s) => new(
        s.Id, $"{s.ReceiptNumber.Series}-{s.ReceiptNumber.Sequence}", s.BusinessDate, s.CompletedAt, s.Status.ToString(),
        s.Currency, s.TotalExclusiveTax.Amount, s.TotalTax.Amount, s.TotalInclusiveTax.Amount,
        s.AmountTendered.Amount, s.ChangeGiven.Amount,
        [.. s.Lines.OrderBy(l => l.LineNumber).Select(l => new SaleLineDetailResponse(
            l.LineNumber, l.VariantId, l.Description, l.Quantity, l.UnitOfMeasure,
            l.UnitPrice.Amount, l.DiscountAmount.Amount, l.NetAmount.Amount, l.TaxAmount.Amount, l.GrossAmount.Amount))],
        [.. s.Tenders.Select(t => new SaleTenderDetailResponse(t.Method.ToString(), t.Amount.Amount, t.Reference))]);
}

internal static class SalesEndpointErrors
{
    public static Error ShiftAlreadyOpenForTerminal => Error.Conflict(
        "sales.shift.already_open", "This terminal already has an open shift.");

    public static Error ShiftNotFound => Error.NotFound(
        "sales.shift.not_found", "No shift with that id.");

    public static Error UnknownVariant => Error.Validation(
        "sales.line.unknown_variant", "One or more lines reference a product variant that does not exist.");

    public static Error PriceCurrencyMismatch(string sku, string priceCurrency, string saleCurrency) => Error.Validation(
        "sales.line.price_currency_mismatch",
        $"'{sku}' is priced in {priceCurrency}, but this sale is in {saleCurrency}.");

    public static Error UnknownTenderMethod(string method) => Error.Validation(
        "sales.tender.unknown_method", $"'{method}' is not a recognised tender method.");

    public static Error ReceiptNumberConflict => Error.Conflict(
        "sales.receipt_number_conflict", "Another sale on this terminal took this receipt number first. Retry the checkout.");
}
