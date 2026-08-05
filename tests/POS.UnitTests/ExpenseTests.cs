using POS.Expenses.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class ExpenseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 22);
    private const string Gbp = "GBP";

    private static Money M(decimal amount) => new(amount, Gbp);

    private static Expense Record(
        ExpenseCategory category,
        Guid recordedBy,
        decimal amount = 100m,
        decimal tax = 20m) =>
        Expense.Record(
            tenantId: Guid.CreateVersion7(),
            companyId: Guid.CreateVersion7(),
            branchId: Guid.CreateVersion7(),
            expenseNumber: "EXP-0001",
            category: category,
            amount: M(amount),
            taxAmount: M(tax),
            incurredOn: Today,
            recordedByUserId: recordedBy,
            recordedAt: Now,
            description: "Pallet delivery from the port");

    [Fact]
    public void Tax_is_held_separately_from_the_net_amount_because_it_is_usually_recoverable()
    {
        var expense = Record(ExpenseCategory.Freight, Guid.CreateVersion7(), amount: 100m, tax: 20m);

        expense.Amount.ShouldBe(M(100m));
        expense.TaxAmount.ShouldBe(M(20m));
        expense.GrossAmount.ShouldBe(M(120m));
    }

    [Fact]
    public void Freight_may_be_capitalised_into_the_cost_of_a_delivery()
    {
        var expense = Record(ExpenseCategory.Freight, Guid.CreateVersion7());

        var linked = expense.LinkToGoodsReceipt(Guid.CreateVersion7());

        linked.IsSuccess.ShouldBeTrue();
        expense.IsCapitalised.ShouldBeTrue();
    }

    [Fact]
    public void Customs_duty_may_be_capitalised_too()
    {
        var expense = Record(ExpenseCategory.CustomsDuty, Guid.CreateVersion7());

        expense.LinkToGoodsReceipt(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_electricity_bill_may_not_be_capitalised_into_stock_however_convenient_that_would_be()
    {
        // Capitalising overheads into stock value defers the cost and flatters this
        // period's margin. It is a real temptation and a real audit finding, so it is
        // refused by the domain rather than left to a reviewer noticing.
        var expense = Record(ExpenseCategory.Utilities, Guid.CreateVersion7());

        var linked = expense.LinkToGoodsReceipt(Guid.CreateVersion7());

        linked.IsFailure.ShouldBeTrue();
        linked.Error.ShouldBe(ExpenseErrors.CategoryCannotBeCapitalised);
        expense.IsCapitalised.ShouldBeFalse();
    }

    [Fact]
    public void The_capitalisable_categories_are_a_closed_list_and_not_a_flag_a_user_can_tick()
    {
        ExpenseCategory.Freight.CanBeCapitalised().ShouldBeTrue();
        ExpenseCategory.CustomsDuty.CanBeCapitalised().ShouldBeTrue();

        foreach (var category in Enum.GetValues<ExpenseCategory>()
                     .Where(c => c is not (ExpenseCategory.Freight or ExpenseCategory.CustomsDuty)))
        {
            category.CanBeCapitalised().ShouldBeFalse($"{category} must not be capitalisable");
        }
    }

    [Fact]
    public void An_expense_cannot_be_approved_by_the_person_who_recorded_it()
    {
        var clerk = Guid.CreateVersion7();
        var expense = Record(ExpenseCategory.Travel, clerk);

        var approved = expense.Approve(clerk, Now);

        approved.IsFailure.ShouldBeTrue();
        approved.Error.ShouldBe(ExpenseErrors.SelfApprovalForbidden);
        expense.Status.ShouldBe(ExpenseStatus.Recorded);
    }

    [Fact]
    public void Someone_else_may_approve_it_and_is_recorded_as_having_done_so()
    {
        var clerk = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();
        var expense = Record(ExpenseCategory.Travel, clerk);

        expense.Approve(manager, Now).IsSuccess.ShouldBeTrue();

        expense.Status.ShouldBe(ExpenseStatus.Approved);
        expense.ApprovedByUserId.ShouldBe(manager);
    }

    [Fact]
    public void An_approved_expense_cannot_be_approved_or_rejected_again()
    {
        var expense = Record(ExpenseCategory.Travel, Guid.CreateVersion7());
        expense.Approve(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        expense.Approve(Guid.CreateVersion7(), Now).Error.ShouldBe(ExpenseErrors.ExpenseNotPending);
        expense.Reject("Changed my mind").Error.ShouldBe(ExpenseErrors.ExpenseNotPending);
    }

    [Fact]
    public void Rejecting_an_expense_requires_a_reason()
    {
        var expense = Record(ExpenseCategory.Marketing, Guid.CreateVersion7());

        expense.Reject("  ").Error.ShouldBe(ExpenseErrors.RejectionReasonRequired);

        expense.Reject("No receipt attached").IsSuccess.ShouldBeTrue();
        expense.Status.ShouldBe(ExpenseStatus.Rejected);
        expense.RejectionReason.ShouldBe("No receipt attached");
    }

    [Fact]
    public void A_rejected_freight_expense_cannot_then_be_capitalised_into_a_delivery()
    {
        var expense = Record(ExpenseCategory.Freight, Guid.CreateVersion7());
        expense.Reject("Duplicate of EXP-0004").IsSuccess.ShouldBeTrue();

        expense.LinkToGoodsReceipt(Guid.CreateVersion7()).Error.ShouldBe(ExpenseErrors.ExpenseRejected);
    }

    [Fact]
    public void A_zero_or_negative_expense_is_rejected_at_construction_because_it_is_not_an_expense()
    {
        Should.Throw<ArgumentException>(() => Record(ExpenseCategory.Rent, Guid.CreateVersion7(), amount: 0m));
        Should.Throw<ArgumentException>(() => Record(ExpenseCategory.Rent, Guid.CreateVersion7(), amount: -5m));
    }

    [Fact]
    public void Tax_must_be_in_the_same_currency_as_the_expense()
    {
        Should.Throw<ArgumentException>(() => Expense.Record(
            tenantId: Guid.CreateVersion7(),
            companyId: Guid.CreateVersion7(),
            branchId: Guid.CreateVersion7(),
            expenseNumber: "EXP-0002",
            category: ExpenseCategory.Travel,
            amount: M(100m),
            taxAmount: new Money(20m, "EUR"),
            incurredOn: Today,
            recordedByUserId: Guid.CreateVersion7(),
            recordedAt: Now,
            description: "Eurostar"));
    }
}
