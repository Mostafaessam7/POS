using POS.SharedKernel;

namespace POS.Expenses.Domain;

/// <summary>
/// Money the business spent that is not the cost of goods.
/// </summary>
/// <remarks>
/// A deliberately small module. The temptation is to grow this into an accounts-payable
/// ledger, and the reason not to is that most tenants already have an accounting package
/// and will not thank us for a second, worse one. What a POS genuinely needs is the
/// ability to record a spend at the shop — the plumber, the window cleaner, the taxi with
/// the urgent stock — attribute it to a branch and a category, and export it. That is the
/// scope.
///
/// The one place this touches Purchasing is <see cref="LinkedGoodsReceiptId"/>. A freight
/// invoice is simultaneously an expense and a landed cost, and the link is what stops it
/// being entered twice by two people who each believe they own it.
/// </remarks>
public sealed class Expense : AggregateRoot<Guid>, ITenantScoped, IBranchScoped, ICompanyScoped
{
    private Expense() { }

    public static Expense Record(
        Guid tenantId,
        Guid companyId,
        Guid branchId,
        string expenseNumber,
        ExpenseCategory category,
        Money amount,
        Money taxAmount,
        DateOnly incurredOn,
        Guid recordedByUserId,
        DateTimeOffset recordedAt,
        string description,
        Guid? supplierId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expenseNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (amount.IsNegative || amount.IsZero)
        {
            throw new ArgumentException("An expense must be a positive amount.", nameof(amount));
        }

        if (taxAmount.Currency != amount.Currency)
        {
            throw new ArgumentException("Tax must be in the same currency as the expense.", nameof(taxAmount));
        }

        return new Expense
        {
            Id = SequentialId.New(),
            TenantId = tenantId,
            CompanyId = companyId,
            BranchId = branchId,
            ExpenseNumber = expenseNumber,
            Category = category,
            Amount = amount,
            TaxAmount = taxAmount,
            IncurredOn = incurredOn,
            RecordedByUserId = recordedByUserId,
            RecordedAt = recordedAt,
            Description = description.Trim(),
            SupplierId = supplierId,
            Status = ExpenseStatus.Recorded
        };
    }

    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }

    public string ExpenseNumber { get; private set; } = string.Empty;
    public ExpenseCategory Category { get; private set; }

    /// <summary>Net amount, excluding tax.</summary>
    public Money Amount { get; private set; }

    /// <summary>
    /// Recoverable tax, held separately.
    /// </summary>
    /// <remarks>
    /// Separated rather than stored gross because in most VAT jurisdictions the tax on a
    /// business expense is reclaimable, and a gross figure makes the return impossible to
    /// prepare without re-deriving a number we were told at entry time.
    /// </remarks>
    public Money TaxAmount { get; private set; }

    public Money GrossAmount => Amount + TaxAmount;

    public DateOnly IncurredOn { get; private set; }
    public string Description { get; private set; } = string.Empty;

    /// <summary>Where the money went, when it went to a supplier we already know about.</summary>
    public Guid? SupplierId { get; private set; }

    /// <summary>
    /// The delivery this expense forms part of the landed cost of, if any.
    /// </summary>
    /// <remarks>
    /// Set only for expense categories that can be capitalised into stock —
    /// <see cref="ExpenseCategory.Freight"/> and <see cref="ExpenseCategory.CustomsDuty"/>.
    /// Once linked the expense must not also be expensed to the profit and loss, or the
    /// cost is counted twice: once in the value of the stock and once as an overhead.
    /// <see cref="IsCapitalised"/> is what a later export reads to decide.
    /// </remarks>
    public Guid? LinkedGoodsReceiptId { get; private set; }

    public bool IsCapitalised => LinkedGoodsReceiptId is not null;

    public Guid RecordedByUserId { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    public ExpenseStatus Status { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string? RejectionReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Attaches this expense to a delivery so it can be allocated as a landed cost.
    /// </summary>
    public Result LinkToGoodsReceipt(Guid goodsReceiptId)
    {
        if (!Category.CanBeCapitalised())
        {
            // Capitalising the electricity bill into stock value is a genuine, and
            // genuinely tempting, way to flatter a margin report. Refused at the domain
            // level rather than left to a reviewer's attention.
            return Result.Failure(ExpenseErrors.CategoryCannotBeCapitalised);
        }

        if (Status == ExpenseStatus.Rejected)
        {
            return Result.Failure(ExpenseErrors.ExpenseRejected);
        }

        LinkedGoodsReceiptId = goodsReceiptId;
        return Result.Success();
    }

    public Result Approve(Guid approverUserId, DateTimeOffset approvedAt)
    {
        if (Status != ExpenseStatus.Recorded)
        {
            return Result.Failure(ExpenseErrors.ExpenseNotPending);
        }

        if (approverUserId == RecordedByUserId)
        {
            // Same control as purchase order approval, and for the same reason. Expenses
            // are the smaller and softer of the two routes money leaves a business by,
            // which is exactly why they are the less watched one.
            return Result.Failure(ExpenseErrors.SelfApprovalForbidden);
        }

        Status = ExpenseStatus.Approved;
        ApprovedByUserId = approverUserId;
        ApprovedAt = approvedAt;
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        if (Status != ExpenseStatus.Recorded)
        {
            return Result.Failure(ExpenseErrors.ExpenseNotPending);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(ExpenseErrors.RejectionReasonRequired);
        }

        Status = ExpenseStatus.Rejected;
        RejectionReason = reason.Trim();
        return Result.Success();
    }
}

public enum ExpenseStatus
{
    Recorded = 1,
    Approved = 2,
    Rejected = 3
}

public enum ExpenseCategory
{
    Freight = 1,
    CustomsDuty = 2,
    Rent = 10,
    Utilities = 11,
    Maintenance = 12,
    Marketing = 13,
    ProfessionalFees = 14,
    Travel = 15,
    OfficeSupplies = 16,
    BankCharges = 17,
    Other = 99
}

public static class ExpenseCategoryExtensions
{
    /// <summary>
    /// Whether this category may be capitalised into the value of stock.
    /// </summary>
    /// <remarks>
    /// A closed list rather than a flag on the record, because the answer is a property of
    /// the category itself and should not be something a user can tick.
    /// </remarks>
    public static bool CanBeCapitalised(this ExpenseCategory category) =>
        category is ExpenseCategory.Freight or ExpenseCategory.CustomsDuty;
}

public static class ExpenseErrors
{
    public static readonly Error CategoryCannotBeCapitalised = Error.BusinessRule(
        "expense.category_cannot_be_capitalised",
        "Only freight and customs duty may be allocated to a delivery as a landed cost.");

    public static readonly Error ExpenseNotPending = Error.Conflict(
        "expense.not_pending",
        "This expense has already been approved or rejected.");

    public static readonly Error ExpenseRejected = Error.Conflict(
        "expense.rejected",
        "A rejected expense cannot be linked to a delivery.");

    public static readonly Error SelfApprovalForbidden = Error.Forbidden(
        "expense.self_approval_forbidden",
        "An expense must be approved by someone other than the person who recorded it.");

    public static readonly Error RejectionReasonRequired = Error.Validation(
        "expense.rejection_reason_required",
        "A reason is required when rejecting an expense.");
}
