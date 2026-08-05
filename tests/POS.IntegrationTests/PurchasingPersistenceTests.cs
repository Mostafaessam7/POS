using Microsoft.EntityFrameworkCore;
using POS.Expenses.Domain;
using POS.Expenses.Persistence;
using POS.Purchasing.Domain;
using POS.Purchasing.Persistence;
using POS.SharedKernel;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Proves the Purchasing and Expenses mappings actually round-trip.
/// </summary>
/// <remarks>
/// A migration that applies proves the schema is legal SQL, not that the model is
/// right. These two modules were mapped without any test writing a row, which is the
/// state the whole repository was in before the executable-baseline milestone and is
/// exactly how a mapping bug survives to production.
///
/// The cases are chosen for the mappings most likely to be wrong rather than for
/// coverage: the composite keys on dependents that have no surrogate id, the Money
/// complex properties on records that needed an EF materialisation constructor, and
/// the one nullable Money in the codebase, which is stored through a value converter
/// because EF Core 9 cannot express an optional complex type.
///
/// Each test writes through one scope and reads through ANOTHER, so nothing is served
/// from the change tracker. Reading back the same instance would pass even if the
/// mapping wrote nothing at all.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class PurchasingPersistenceTests(ApiFixture fixture)
{
    private const string Currency = "USD";

    [Fact]
    public async Task Supplier_round_trips_with_its_terms_and_product_codes()
    {
        var tenantId = Guid.CreateVersion7();
        var companyId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var code = $"SUP-{Guid.CreateVersion7():N}"[..20];

        var supplier = Supplier.Create(
            tenantId, companyId, code, "Acme Supplies", Currency,
            new SupplierTerms(paymentTermDays: 45, leadTimeDays: 10, minimumOrderValue: 250m));

        supplier.AddProductCode(variantId, "ACME-991", packSize: 12m, description: "Case of 12")
                .IsSuccess.ShouldBeTrue();

        await fixture.WriteAsync<PurchasingDbContext>(tenantId, db => db.Suppliers.Add(supplier));

        var reloaded = await fixture.ReadAsync<PurchasingDbContext, Supplier?>(tenantId, db =>
            db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplier.Id));

        reloaded.ShouldNotBeNull();
        reloaded.Terms.PaymentTermDays.ShouldBe(45);
        reloaded.Terms.MinimumOrderValue.ShouldBe(250m);

        var productCode = reloaded.FindProductCode(variantId);
        productCode.ShouldNotBeNull();
        productCode.Code.ShouldBe("ACME-991");
        productCode.PackSize.ShouldBe(12m);
    }

    /// <summary>
    /// Lines and landed costs are dependents keyed without a surrogate, and both carry
    /// Money. If the composite keys or the complex properties are wrong, this is where
    /// it shows.
    /// </summary>
    [Fact]
    public async Task Goods_receipt_round_trips_with_lines_and_landed_costs()
    {
        var tenantId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var receipt = GoodsReceipt.Create(
            tenantId,
            branchId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            purchaseOrderId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            receiptNumber: $"GRN-{Guid.CreateVersion7():N}"[..20],
            currency: Currency,
            supplierDeliveryNote: "DN-4471",
            receivedByUserId: Guid.CreateVersion7(),
            receivedAt: DateTimeOffset.UtcNow,
            businessDate: BusinessDate.Open(DateOnly.FromDateTime(DateTime.UtcNow)));

        receipt.AddLine(1, variantId, quantityReceived: 60m, new Money(10.00m, Currency)).IsSuccess.ShouldBeTrue();
        receipt.AddLandedCost(LandedCostType.Freight, new Money(60.00m, Currency), "FRT-1", LandedCostAllocationBasis.Value)
               .IsSuccess.ShouldBeTrue();

        await fixture.WriteAsync<PurchasingDbContext>(tenantId, db => db.GoodsReceipts.Add(receipt));

        var reloaded = await fixture.ReadAsync<PurchasingDbContext, GoodsReceipt?>(tenantId, db =>
            db.GoodsReceipts
              .AsNoTracking()
              .Include(r => r.Lines)
              .Include(r => r.LandedCosts)
              .FirstOrDefaultAsync(r => r.Id == receipt.Id));

        reloaded.ShouldNotBeNull();
        reloaded.Lines.Count.ShouldBe(1);
        reloaded.Lines[0].QuantityReceived.ShouldBe(60m);
        reloaded.Lines[0].UnitPrice.ShouldBe(new Money(10.00m, Currency));

        reloaded.LandedCosts.Count.ShouldBe(1);
        reloaded.LandedCosts[0].Amount.ShouldBe(new Money(60.00m, Currency));
        reloaded.LandedCosts[0].Type.ShouldBe(LandedCostType.Freight);

        // The number the whole landed-cost design exists to produce.
        reloaded.GoodsValue.ShouldBe(new Money(600.00m, Currency));
        reloaded.LandedCostTotal.ShouldBe(new Money(60.00m, Currency));
    }

    /// <summary>
    /// The one nullable Money in the codebase, stored through a value converter.
    /// </summary>
    /// <remarks>
    /// Both states matter and both are asserted. Null must survive as null — "no credit
    /// note yet" is a different fact from "credited zero", and a converter that
    /// collapses them would silently mark every outstanding claim as settled.
    /// </remarks>
    [Fact]
    public async Task Supplier_return_round_trips_an_absent_and_a_present_credit_note()
    {
        var tenantId = Guid.CreateVersion7();

        var supplierReturn = SupplierReturn.Create(
            tenantId,
            branchId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            returnNumber: $"RTN-{Guid.CreateVersion7():N}"[..20],
            currency: Currency,
            reason: SupplierReturnReason.Damaged,
            raisedByUserId: Guid.CreateVersion7(),
            raisedAt: DateTimeOffset.UtcNow,
            businessDate: BusinessDate.Open(DateOnly.FromDateTime(DateTime.UtcNow)));

        supplierReturn.AddLine(Guid.CreateVersion7(), quantity: 3m, new Money(12.3456m, Currency))
                      .IsSuccess.ShouldBeTrue();

        await fixture.WriteAsync<PurchasingDbContext>(tenantId, db => db.SupplierReturns.Add(supplierReturn));

        var beforeCredit = await fixture.ReadAsync<PurchasingDbContext, SupplierReturn?>(tenantId, db =>
            db.SupplierReturns.AsNoTracking().Include(r => r.Lines)
              .FirstOrDefaultAsync(r => r.Id == supplierReturn.Id));

        beforeCredit.ShouldNotBeNull();
        beforeCredit.CreditedAmount.ShouldBeNull();

        // Four decimal places, deliberately: a converter that formats through a
        // shortened numeric form would round this and lose money.
        beforeCredit.Lines[0].UnitCost.ShouldBe(new Money(12.3456m, Currency));

        supplierReturn.Dispatch(DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        supplierReturn.RecordCreditNote("CN-8812", new Money(37.0368m, Currency), DateOnly.FromDateTime(DateTime.UtcNow))
                      .IsSuccess.ShouldBeTrue();

        await fixture.WriteAsync<PurchasingDbContext>(tenantId, db => db.SupplierReturns.Update(supplierReturn));

        var afterCredit = await fixture.ReadAsync<PurchasingDbContext, SupplierReturn?>(tenantId, db =>
            db.SupplierReturns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == supplierReturn.Id));

        afterCredit.ShouldNotBeNull();
        afterCredit.CreditedAmount.ShouldBe(new Money(37.0368m, Currency));
        afterCredit.CreditNoteNumber.ShouldBe("CN-8812");
    }

    [Fact]
    public async Task Expense_round_trips_both_of_its_money_amounts()
    {
        var tenantId = Guid.CreateVersion7();

        var expense = Expense.Record(
            tenantId,
            companyId: Guid.CreateVersion7(),
            branchId: Guid.CreateVersion7(),
            expenseNumber: $"EXP-{Guid.CreateVersion7():N}"[..20],
            category: ExpenseCategory.Utilities,
            amount: new Money(431.20m, Currency),
            taxAmount: new Money(60.37m, Currency),
            incurredOn: DateOnly.FromDateTime(DateTime.UtcNow),
            recordedByUserId: Guid.CreateVersion7(),
            recordedAt: DateTimeOffset.UtcNow,
            description: "Quarterly electricity");

        await fixture.WriteAsync<ExpensesDbContext>(tenantId, db => db.Expenses.Add(expense));

        var reloaded = await fixture.ReadAsync<ExpensesDbContext, Expense?>(tenantId, db =>
            db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expense.Id));

        reloaded.ShouldNotBeNull();
        reloaded.Amount.ShouldBe(new Money(431.20m, Currency));
        reloaded.TaxAmount.ShouldBe(new Money(60.37m, Currency));

        // Computed, not stored — mapping it would let the parts and the total disagree.
        reloaded.GrossAmount.ShouldBe(new Money(491.57m, Currency));
        reloaded.IsCapitalised.ShouldBeFalse();
    }

    /// <summary>The tenant filter is not bypassed just because these rows were seeded directly.</summary>
    [Fact]
    public async Task Another_tenants_expense_is_invisible()
    {
        var owner = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();

        var expense = Expense.Record(
            owner,
            companyId: Guid.CreateVersion7(),
            branchId: Guid.CreateVersion7(),
            expenseNumber: $"EXP-{Guid.CreateVersion7():N}"[..20],
            category: ExpenseCategory.Rent,
            amount: new Money(1000m, Currency),
            taxAmount: Money.Zero(Currency),
            incurredOn: DateOnly.FromDateTime(DateTime.UtcNow),
            recordedByUserId: Guid.CreateVersion7(),
            recordedAt: DateTimeOffset.UtcNow,
            description: "Shop rent");

        await fixture.WriteAsync<ExpensesDbContext>(owner, db => db.Expenses.Add(expense));

        var seenByStranger = await fixture.ReadAsync<ExpensesDbContext, Expense?>(stranger, db =>
            db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expense.Id));

        seenByStranger.ShouldBeNull();
    }
}
