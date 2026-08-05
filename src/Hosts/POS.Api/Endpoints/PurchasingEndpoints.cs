using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Security;
using POS.Common.Tenancy;
using POS.Common.Validation;
using POS.Identity.Authorization;
using POS.Purchasing;
using POS.Purchasing.Domain;
using POS.Purchasing.Persistence;
using POS.Purchasing.Posting;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// The Purchasing API: suppliers, orders, receipts, invoices and returns.
/// </summary>
/// <remarks>
/// <para>
/// Every route is thin on purpose. Load the aggregate, call the method, map the result.
/// The rules about who may approve what, whether a receipt is within tolerance, and how
/// a landed cost is apportioned all live in the domain and are covered by unit tests;
/// duplicating any of that here would give the system two answers to the same question.
/// </para>
/// <para>
/// AUTHORIZATION IS IN TWO PARTS. <c>RequirePermission</c> gates the endpoint on holding
/// the capability at all. Handlers that name a branch or company then check the
/// permission AT THAT SCOPE, because the policy pipeline cannot see the request body.
/// A user who is a supervisor at one branch must not be able to act on another's, and
/// only the second check can tell.
/// </para>
/// </remarks>
public static class PurchasingEndpoints
{
    /// <summary>The approval ladder, lowest first. Order is what makes "highest held" meaningful.</summary>
    private static readonly (string PermissionCode, ApprovalLevel Level)[] ApprovalLadder =
    [
        (Permissions.Purchasing.OrderApproveSupervisor, ApprovalLevel.Supervisor),
        (Permissions.Purchasing.OrderApproveManager, ApprovalLevel.Manager),
        (Permissions.Purchasing.OrderApproveDirector, ApprovalLevel.Director)
    ];

    private static readonly Error ScopeDenied = Error.Forbidden(
        "purchasing.scope_denied",
        "You do not hold that permission at this branch or company.");

    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/purchasing").RequireAuthorization();

        MapSuppliers(group);
        MapPurchaseOrders(group);
        MapGoodsReceipts(group);
        MapPurchaseInvoices(group);
        MapSupplierReturns(group);

        return app;
    }

    private static void MapSuppliers(IEndpointRouteBuilder group)
    {
        group.MapGet("/suppliers", async (PurchasingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Suppliers
                .AsNoTracking()
                .OrderBy(s => s.Code)
                .Select(s => new SupplierResponse(
                    s.Id, s.Code, s.Name, s.Currency, s.IsActive,
                    s.Terms.PaymentTermDays, s.Terms.LeadTimeDays))
                .ToListAsync(ct)))
        .RequirePermission(Permissions.Purchasing.SupplierView);

        group.MapGet("/suppliers/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
        {
            var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

            return supplier is null
                ? Results.NotFound()
                : Results.Ok(new SupplierResponse(
                    supplier.Id, supplier.Code, supplier.Name, supplier.Currency, supplier.IsActive,
                    supplier.Terms.PaymentTermDays, supplier.Terms.LeadTimeDays));
        })
        .RequirePermission(Permissions.Purchasing.SupplierView);

        group.MapPost("/suppliers", async (
            CreateSupplierRequest request,
            PurchasingDbContext db,
            ITenantContext tenant,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.SupplierManage, request.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var supplier = Supplier.Create(
                tenant.TenantId,
                request.CompanyId,
                request.Code,
                request.Name,
                request.Currency,
                new SupplierTerms(request.PaymentTermDays, request.LeadTimeDays, request.MinimumOrderValue));

            supplier.SetTaxRegistration(request.TaxRegistrationNumber);

            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/purchasing/suppliers/{supplier.Id}", new CreatedResource(supplier.Id));
        })
        .AddValidation<CreateSupplierRequest>()
        .RequirePermission(Permissions.Purchasing.SupplierManage);

        group.MapPost("/suppliers/{id:guid}/product-codes", async (
            Guid id,
            AddSupplierProductCodeRequest request,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);

            if (supplier is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.SupplierManage, supplier.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var added = supplier.AddProductCode(
                request.VariantId, request.Code, request.PackSize, request.Description);

            if (added.IsFailure)
                return added.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .AddValidation<AddSupplierProductCodeRequest>()
        .RequirePermission(Permissions.Purchasing.SupplierManage);

        group.MapPost("/suppliers/{id:guid}/deactivate", async (
            Guid id,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);

            if (supplier is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.SupplierManage, supplier.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            // Deactivated, never deleted: open orders and historical invoices reference
            // this supplier and a hard delete would orphan all of them.
            supplier.Deactivate();
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
        .RequirePermission(Permissions.Purchasing.SupplierManage);
    }

    private static void MapPurchaseOrders(IEndpointRouteBuilder group)
    {
        group.MapGet("/orders", async (PurchasingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.PurchaseOrders
                .AsNoTracking()
                .Include(o => o.Lines)
                .OrderByDescending(o => o.RaisedAt)
                .Take(200)
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(ToResponse).ToList(), ct)))
        .RequirePermission(Permissions.Purchasing.OrderView);

        group.MapGet("/orders/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
        {
            var order = await db.PurchaseOrders
                .AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == id, ct);

            return order is null ? Results.NotFound() : Results.Ok(ToResponse(order));
        })
        .RequirePermission(Permissions.Purchasing.OrderView);

        group.MapPost("/orders", async (
            RaisePurchaseOrderRequest request,
            PurchasingDbContext db,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            PurchasingPolicyResolver policyResolver,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.OrderRaise, request.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var supplier = await db.Suppliers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct);

            if (supplier is null)
                return Error.NotFound("purchasing.supplier.unknown", "The supplier does not exist.").ToHttpResult();

            var order = PurchaseOrder.Raise(
                tenant.TenantId,
                request.CompanyId,
                request.BranchId,
                request.WarehouseId,
                supplier,
                request.OrderNumber,
                currentUser.UserId ?? Guid.Empty,
                clock.UtcNow,
                BusinessDate.Open(request.BusinessDate));

            foreach (var line in request.Lines)
            {
                var added = order.AddLine(
                    line.VariantId,
                    line.Quantity,
                    new Money(line.UnitPrice, supplier.Currency),
                    line.SupplierCode,
                    line.Description);

                if (added.IsFailure)
                    return added.Error.ToHttpResult();
            }

            // Submitted immediately. An order that is raised but never submitted sits
            // invisible to the approver, and the domain decides from the value whether
            // that means PendingApproval or straight to Approved.
            var policy = await policyResolver.ResolveAsync(tenant.TenantId, ct);
            var submitted = order.Submit(policy.ApprovalPolicyFor(supplier.Currency), clock.UtcNow);

            if (submitted.IsFailure)
                return submitted.Error.ToHttpResult();

            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/purchasing/orders/{order.Id}", ToResponse(order));
        })
        .AddValidation<RaisePurchaseOrderRequest>()
        .RequirePermission(Permissions.Purchasing.OrderRaise);

        // No RequirePermission for a single code: the ladder IS the gate. A user holding
        // none of the three levels gets 403 from the scope check below.
        group.MapPost("/orders/{id:guid}/approve", async (
            Guid id,
            PurchasingDbContext db,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            PurchasingPolicyResolver policyResolver,
            CancellationToken ct) =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

            if (order is null)
                return Results.NotFound();

            var level = await scope.HighestHeldAsync(ApprovalLadder, order.BranchId, ct);

            if (level is null)
                return ScopeDenied.ToHttpResult();

            // Whether this level is high enough for the order's value, and whether the
            // raiser is allowed to approve their own order, are both the aggregate's
            // decisions (ADR 049, ADR 050).
            var policy = await policyResolver.ResolveAsync(db.CurrentTenantId, ct);
            var approved = order.Approve(
                policy.ApprovalPolicyFor(order.Currency),
                currentUser.UserId ?? Guid.Empty,
                level.Value,
                clock.UtcNow);

            if (approved.IsFailure)
                return approved.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(order));
        });

        group.MapPost("/orders/{id:guid}/send", async (
            Guid id,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

            if (order is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.OrderRaise, order.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var sent = order.Send(clock.UtcNow);

            if (sent.IsFailure)
                return sent.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(order));
        })
        .RequirePermission(Permissions.Purchasing.OrderRaise);

        group.MapPost("/orders/{id:guid}/cancel", async (
            Guid id,
            CancelRequest request,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

            if (order is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.OrderCancel, order.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var cancelled = order.Cancel(request.Reason, clock.UtcNow);

            if (cancelled.IsFailure)
                return cancelled.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(order));
        })
        .AddValidation<CancelRequest>()
        .RequirePermission(Permissions.Purchasing.OrderCancel);
    }

    private static void MapGoodsReceipts(IEndpointRouteBuilder group)
    {
        group.MapGet("/receipts/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
        {
            var receipt = await db.GoodsReceipts
                .AsNoTracking()
                .Include(r => r.Lines)
                .Include(r => r.LandedCosts)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            return receipt is null ? Results.NotFound() : Results.Ok(ToResponse(receipt));
        })
        .RequirePermission(Permissions.Purchasing.ReceiptView);

        group.MapPost("/receipts", async (
            CreateGoodsReceiptRequest request,
            PurchasingDbContext db,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var order = await db.PurchaseOrders.AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == request.PurchaseOrderId, ct);

            if (order is null)
                return Error.NotFound("purchasing.order.unknown", "The purchase order does not exist.").ToHttpResult();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.ReceiptCreate, order.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var receipt = GoodsReceipt.Create(
                tenant.TenantId,
                order.BranchId,
                order.WarehouseId,
                order.Id,
                order.SupplierId,
                request.ReceiptNumber,
                order.Currency,
                request.SupplierDeliveryNote,
                currentUser.UserId ?? Guid.Empty,
                clock.UtcNow,
                BusinessDate.Open(request.BusinessDate));

            foreach (var line in request.Lines)
            {
                var added = receipt.AddLine(
                    line.PurchaseOrderLineNumber,
                    line.VariantId,
                    line.QuantityReceived,
                    new Money(line.UnitPrice, order.Currency));

                if (added.IsFailure)
                    return added.Error.ToHttpResult();
            }

            foreach (var cost in request.LandedCosts ?? [])
            {
                var added = receipt.AddLandedCost(
                    cost.Type, new Money(cost.Amount, order.Currency), cost.Reference, cost.Basis);

                if (added.IsFailure)
                    return added.Error.ToHttpResult();
            }

            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/purchasing/receipts/{receipt.Id}", ToResponse(receipt));
        })
        .AddValidation<CreateGoodsReceiptRequest>()
        .RequirePermission(Permissions.Purchasing.ReceiptCreate);

        // Posting is separate from creating, and that separation is the point: a receipt
        // is captured at the loading bay and posted once someone has checked it against
        // the order. Posting is what moves stock and fixes landed cost.
        group.MapPost("/receipts/{id:guid}/post", async (
            Guid id,
            PurchasingDbContext db,
            GoodsReceiptPostingService posting,
            IPermissionScopeGuard scope,
            PurchasingPolicyResolver policyResolver,
            CancellationToken ct) =>
        {
            // Loaded here only to authorise against its branch. The service resolves
            // the receipt again for the work itself, so the ordering between the stock
            // movement and the receipt's own save stays entirely inside it.
            var branchId = await db.GoodsReceipts
                .AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => (Guid?)r.BranchId)
                .FirstOrDefaultAsync(ct);

            if (branchId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.ReceiptPost, branchId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var policy = await policyResolver.ResolveAsync(db.CurrentTenantId, ct);
            var result = await posting.PostAsync(id, policy.ReceiptTolerance(), ct);

            if (result.IsFailure)
                return result.Error.ToHttpResult();

            var receipt = await db.GoodsReceipts
                .AsNoTracking()
                .Include(r => r.Lines)
                .Include(r => r.LandedCosts)
                .FirstAsync(r => r.Id == id, ct);

            return Results.Ok(new
            {
                receipt = ToResponse(receipt),
                stockMovements = result.Value.Movements.Count
            });
        })
        .RequirePermission(Permissions.Purchasing.ReceiptPost);
    }

    private static void MapPurchaseInvoices(IEndpointRouteBuilder group)
    {
        group.MapGet("/invoices", async (PurchasingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.PurchaseInvoices
                .AsNoTracking()
                .Include(i => i.Lines)
                .OrderByDescending(i => i.InvoiceDate)
                .Take(200)
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(ToResponse).ToList(), ct)))
        .RequirePermission(Permissions.Purchasing.InvoiceView);

        group.MapGet("/invoices/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
        {
            var invoice = await db.PurchaseInvoices
                .AsNoTracking()
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == id, ct);

            return invoice is null ? Results.NotFound() : Results.Ok(ToResponse(invoice));
        })
        .RequirePermission(Permissions.Purchasing.InvoiceView);

        group.MapPost("/invoices", async (
            RecordPurchaseInvoiceRequest request,
            PurchasingDbContext db,
            ITenantContext tenant,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.InvoiceRecord, request.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var invoice = PurchaseInvoice.Record(
                tenant.TenantId,
                request.CompanyId,
                request.SupplierId,
                request.PurchaseOrderId,
                request.SupplierInvoiceNumber,
                request.Currency,
                request.InvoiceDate,
                request.DueDate,
                clock.UtcNow);

            foreach (var line in request.Lines)
            {
                var added = invoice.AddLine(
                    line.PurchaseOrderLineNumber,
                    line.VariantId,
                    line.Quantity,
                    new Money(line.UnitPrice, request.Currency));

                if (added.IsFailure)
                    return added.Error.ToHttpResult();
            }

            db.PurchaseInvoices.Add(invoice);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/purchasing/invoices/{invoice.Id}", ToResponse(invoice));
        })
        .AddValidation<RecordPurchaseInvoiceRequest>()
        .RequirePermission(Permissions.Purchasing.InvoiceRecord);

        // The three-way match: order says what was agreed, receipts say what arrived,
        // invoice says what is being billed. Any two agreeing is not enough.
        group.MapPost("/invoices/{id:guid}/match", async (
            Guid id,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var invoice = await db.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);

            if (invoice is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.InvoiceApprove, invoice.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var order = await db.PurchaseOrders.AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == invoice.PurchaseOrderId, ct);

            if (order is null)
                return Error.NotFound("purchasing.order.unknown", "The purchase order does not exist.").ToHttpResult();

            var receipts = await db.GoodsReceipts.AsNoTracking()
                .Include(r => r.Lines)
                .Include(r => r.LandedCosts)
                .Where(r => r.PurchaseOrderId == order.Id)
                .ToListAsync(ct);

            var result = ThreeWayMatcher.Match(order, receipts, invoice, MatchTolerance.Default(invoice.Currency));

            invoice.ApplyMatch(result);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ThreeWayMatchResponse(
                result.Outcome,
                result.IsPayable,
                [.. result.Variances.Select(v => new MatchVarianceResponse(
                    v.PurchaseOrderLineNumber, v.Type, v.Billed, v.Expected, v.Describe()))]));
        })
        .RequirePermission(Permissions.Purchasing.InvoiceApprove);

        group.MapPost("/invoices/{id:guid}/approve", async (
            Guid id,
            PurchasingDbContext db,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var invoice = await db.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);

            if (invoice is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.InvoiceApprove, invoice.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var approved = invoice.Approve(currentUser.UserId ?? Guid.Empty, clock.UtcNow);

            if (approved.IsFailure)
                return approved.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(invoice));
        })
        .RequirePermission(Permissions.Purchasing.InvoiceApprove);

        // A SEPARATE, HIGHER permission from approve. Releasing an invoice the match
        // blocked is the fraud-sensitive action in this module, and anyone able to
        // approve a clean invoice must not thereby be able to release a blocked one.
        group.MapPost("/invoices/{id:guid}/override-block", async (
            Guid id,
            OverrideInvoiceBlockRequest request,
            PurchasingDbContext db,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var invoice = await db.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);

            if (invoice is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.InvoiceOverrideBlock, invoice.CompanyId, ct))
                return ScopeDenied.ToHttpResult();

            var overridden = invoice.OverrideBlock(currentUser.UserId ?? Guid.Empty, request.Reason, clock.UtcNow);

            if (overridden.IsFailure)
                return overridden.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(invoice));
        })
        .AddValidation<OverrideInvoiceBlockRequest>()
        .RequirePermission(Permissions.Purchasing.InvoiceOverrideBlock);
    }

    private static void MapSupplierReturns(IEndpointRouteBuilder group)
    {
        group.MapGet("/returns", async (PurchasingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.SupplierReturns
                .AsNoTracking()
                .Include(r => r.Lines)
                .OrderByDescending(r => r.RaisedAt)
                .Take(200)
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(ToResponse).ToList(), ct)))
        .RequirePermission(Permissions.Purchasing.ReturnView);

        group.MapGet("/returns/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
        {
            var supplierReturn = await db.SupplierReturns
                .AsNoTracking()
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            return supplierReturn is null ? Results.NotFound() : Results.Ok(ToResponse(supplierReturn));
        })
        .RequirePermission(Permissions.Purchasing.ReturnView);

        group.MapPost("/returns", async (
            CreateSupplierReturnRequest request,
            PurchasingDbContext db,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.ReturnCreate, request.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var supplierReturn = SupplierReturn.Create(
                tenant.TenantId,
                request.BranchId,
                request.WarehouseId,
                request.SupplierId,
                request.ReturnNumber,
                request.Currency,
                request.Reason,
                currentUser.UserId ?? Guid.Empty,
                clock.UtcNow,
                BusinessDate.Open(request.BusinessDate),
                request.OriginalGoodsReceiptId);

            foreach (var line in request.Lines)
            {
                var added = supplierReturn.AddLine(
                    line.VariantId, line.Quantity, new Money(line.UnitCost, request.Currency));

                if (added.IsFailure)
                    return added.Error.ToHttpResult();
            }

            db.SupplierReturns.Add(supplierReturn);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/purchasing/returns/{supplierReturn.Id}", ToResponse(supplierReturn));
        })
        .AddValidation<CreateSupplierReturnRequest>()
        .RequirePermission(Permissions.Purchasing.ReturnCreate);

        group.MapPost("/returns/{id:guid}/dispatch", async (
            Guid id,
            PurchasingDbContext db,
            SupplierReturnDispatchService dispatch,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var branchId = await db.SupplierReturns
                .AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => (Guid?)r.BranchId)
                .FirstOrDefaultAsync(ct);

            if (branchId is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.ReturnDispatch, branchId.Value, ct))
                return ScopeDenied.ToHttpResult();

            var result = await dispatch.DispatchAsync(id, ct);

            if (result.IsFailure)
                return result.Error.ToHttpResult();

            var supplierReturn = await db.SupplierReturns
                .AsNoTracking()
                .Include(r => r.Lines)
                .FirstAsync(r => r.Id == id, ct);

            return Results.Ok(new
            {
                supplierReturn = ToResponse(supplierReturn),
                stockMovements = result.Value.Movements.Count
            });
        })
        .RequirePermission(Permissions.Purchasing.ReturnDispatch);

        group.MapPost("/returns/{id:guid}/credit-note", async (
            Guid id,
            RecordCreditNoteRequest request,
            PurchasingDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var supplierReturn = await db.SupplierReturns.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            if (supplierReturn is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Purchasing.ReturnRecordCredit, supplierReturn.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var recorded = supplierReturn.RecordCreditNote(
                request.CreditNoteNumber,
                new Money(request.Amount, supplierReturn.Currency),
                request.CreditNoteDate);

            if (recorded.IsFailure)
                return recorded.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(supplierReturn));
        })
        .AddValidation<RecordCreditNoteRequest>()
        .RequirePermission(Permissions.Purchasing.ReturnRecordCredit);
    }

    private static PurchaseOrderResponse ToResponse(PurchaseOrder order) => new(
        order.Id,
        order.OrderNumber,
        order.SupplierId,
        order.Currency,
        order.Status,
        order.TotalValue.Amount,
        order.ExpectedDeliveryDate,
        [.. order.Lines.Select(l => new PurchaseOrderLineResponse(
            l.LineNumber, l.VariantId, l.QuantityOrdered, l.QuantityReceived,
            l.OutstandingQuantity, l.UnitPrice.Amount))]);

    private static GoodsReceiptResponse ToResponse(GoodsReceipt receipt) => new(
        receipt.Id,
        receipt.ReceiptNumber,
        receipt.PurchaseOrderId,
        receipt.Status,
        receipt.GoodsValue.Amount,
        receipt.LandedCostTotal.Amount);

    private static PurchaseInvoiceResponse ToResponse(PurchaseInvoice invoice) => new(
        invoice.Id,
        invoice.SupplierInvoiceNumber,
        invoice.PurchaseOrderId,
        invoice.Status,
        invoice.NetTotal.Amount,
        invoice.BlockReason);

    private static SupplierReturnResponse ToResponse(SupplierReturn supplierReturn) => new(
        supplierReturn.Id,
        supplierReturn.ReturnNumber,
        supplierReturn.SupplierId,
        supplierReturn.Status,
        supplierReturn.ExpectedCredit.Amount,
        supplierReturn.CreditedAmount?.Amount,
        supplierReturn.CreditNoteNumber);
}
