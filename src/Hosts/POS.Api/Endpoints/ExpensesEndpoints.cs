using FluentValidation;
using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Security;
using POS.Common.Tenancy;
using POS.Common.Validation;
using POS.Expenses.Domain;
using POS.Expenses.Persistence;
using POS.Identity.Authorization;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>The Expenses API: record, approve, reject.</summary>
/// <remarks>
/// Capitalisation is NOT an endpoint. Whether an expense becomes part of stock value
/// rather than a period cost depends on it being linked to a goods receipt, and the
/// domain restricts that to a closed list of categories (ADR 053). Exposing a "set
/// capitalised" route would let a caller reclassify spend directly, which is precisely
/// the judgement the closed list exists to remove.
/// </remarks>
public static class ExpensesEndpoints
{
    private static readonly Error ScopeDenied = Error.Forbidden(
        "expenses.scope_denied",
        "You do not hold that permission at this branch.");

    public static IEndpointRouteBuilder MapExpensesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/expenses").RequireAuthorization();

        group.MapGet("/", async (ExpensesDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Expenses
                .AsNoTracking()
                .OrderByDescending(e => e.IncurredOn)
                .Take(200)
                .Select(e => new ExpenseResponse(
                    e.Id, e.ExpenseNumber, e.Category, e.Amount.Amount, e.TaxAmount.Amount,
                    e.IncurredOn, e.Status, e.Description, e.LinkedGoodsReceiptId != null))
                .ToListAsync(ct)))
        .RequirePermission(Permissions.Expenses.View);

        group.MapGet("/{id:guid}", async (Guid id, ExpensesDbContext db, CancellationToken ct) =>
        {
            var expense = await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

            return expense is null ? Results.NotFound() : Results.Ok(ToResponse(expense));
        })
        .RequirePermission(Permissions.Expenses.View);

        group.MapPost("/", async (
            RecordExpenseRequest request,
            ExpensesDbContext db,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Expenses.Record, request.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var expense = Expense.Record(
                tenant.TenantId,
                request.CompanyId,
                request.BranchId,
                request.ExpenseNumber,
                request.Category,
                new Money(request.Amount, request.Currency),
                new Money(request.TaxAmount, request.Currency),
                request.IncurredOn,
                currentUser.UserId ?? Guid.Empty,
                clock.UtcNow,
                request.Description,
                request.SupplierId);

            db.Expenses.Add(expense);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/expenses/{expense.Id}", ToResponse(expense));
        })
        .AddValidation<RecordExpenseRequest>()
        .RequirePermission(Permissions.Expenses.Record);

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            ExpensesDbContext db,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            IClock clock,
            CancellationToken ct) =>
        {
            var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct);

            if (expense is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Expenses.Approve, expense.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            // Self-approval is refused by the aggregate, not here. Someone recording
            // their own travel claim and signing it off is the failure this control
            // exists for, and the rule belongs where it cannot be bypassed by a second
            // caller.
            var approved = expense.Approve(currentUser.UserId ?? Guid.Empty, clock.UtcNow);

            if (approved.IsFailure)
                return approved.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(expense));
        })
        .RequirePermission(Permissions.Expenses.Approve);

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            RejectExpenseRequest request,
            ExpensesDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct);

            if (expense is null)
                return Results.NotFound();

            if (!await scope.HasAtScopeAsync(Permissions.Expenses.Reject, expense.BranchId, ct))
                return ScopeDenied.ToHttpResult();

            var rejected = expense.Reject(request.Reason);

            if (rejected.IsFailure)
                return rejected.Error.ToHttpResult();

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(expense));
        })
        .AddValidation<RejectExpenseRequest>()
        .RequirePermission(Permissions.Expenses.Reject);

        return app;
    }

    private static ExpenseResponse ToResponse(Expense expense) => new(
        expense.Id,
        expense.ExpenseNumber,
        expense.Category,
        expense.Amount.Amount,
        expense.TaxAmount.Amount,
        expense.IncurredOn,
        expense.Status,
        expense.Description,
        expense.IsCapitalised);
}

public sealed record RecordExpenseRequest(
    Guid CompanyId,
    Guid BranchId,
    string ExpenseNumber,
    ExpenseCategory Category,
    decimal Amount,
    decimal TaxAmount,
    string Currency,
    DateOnly IncurredOn,
    string Description,
    Guid? SupplierId = null);

public sealed class RecordExpenseRequestValidator : AbstractValidator<RecordExpenseRequest>
{
    public RecordExpenseRequestValidator()
    {
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.ExpenseNumber).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Category).IsInEnum();
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Description).NotEmpty().MaximumLength(500);

        // A zero-value expense is a data-entry slip, not a business event.
        RuleFor(r => r.Amount).GreaterThan(0m);

        // Tax may legitimately be zero — exempt supplies, or a supplier not registered.
        RuleFor(r => r.TaxAmount).GreaterThanOrEqualTo(0m);
    }
}

public sealed record RejectExpenseRequest(string Reason);

public sealed class RejectExpenseRequestValidator : AbstractValidator<RejectExpenseRequest>
{
    public RejectExpenseRequestValidator() =>
        // The domain requires a reason too. Validating here turns what would be a 422
        // from deep in the aggregate into a 400 naming the field, which is what a client
        // can actually act on.
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(500);
}

public sealed record ExpenseResponse(
    Guid Id,
    string ExpenseNumber,
    ExpenseCategory Category,
    decimal Amount,
    decimal TaxAmount,
    DateOnly IncurredOn,
    ExpenseStatus Status,
    string Description,
    bool IsCapitalised);
