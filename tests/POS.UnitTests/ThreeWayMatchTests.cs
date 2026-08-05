using POS.Purchasing.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// Helpers that build a purchase order through to a state where goods can be received,
/// so the matching tests are about matching rather than about workflow plumbing.
/// </summary>
internal static class MatchFixtures
{
    public static readonly Guid Widget = Guid.CreateVersion7();
    public static readonly Guid Gadget = Guid.CreateVersion7();

    public static Money M(decimal amount) => new(amount, PurchasingFixtures.Gbp);

    /// <summary>An order for 100 widgets at 10.00, approved and sent to the supplier.</summary>
    public static PurchaseOrder SentOrder(decimal quantity = 100m, decimal unitPrice = 10m)
    {
        var supplier = PurchasingFixtures.Supplier();
        var buyer = Guid.CreateVersion7();
        var order = PurchasingFixtures.Order(supplier, buyer);

        order.AddLine(Widget, quantity, M(unitPrice)).IsSuccess.ShouldBeTrue();

        var policy = PurchasingFixtures.Policy();
        order.Submit(policy, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        order.Approve(policy, Guid.CreateVersion7(), ApprovalLevel.Manager, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        order.Send(PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        return order;
    }

    /// <summary>Receives a quantity against line 1 and posts it.</summary>
    public static GoodsReceipt PostedReceipt(PurchaseOrder order, string number, decimal quantity, decimal unitPrice = 10m)
    {
        var receipt = PurchasingFixtures.Receipt(order, number);
        receipt.AddLine(1, Widget, quantity, M(unitPrice)).IsSuccess.ShouldBeTrue();
        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        return receipt;
    }

    public static PurchaseInvoice Invoice(PurchaseOrder order, string currency = PurchasingFixtures.Gbp) =>
        PurchaseInvoice.Record(
            tenantId: order.TenantId,
            companyId: order.CompanyId,
            supplierId: order.SupplierId,
            purchaseOrderId: order.Id,
            supplierInvoiceNumber: "SI-4471",
            currency: currency,
            invoiceDate: PurchasingFixtures.Today.Value,
            dueDate: PurchasingFixtures.Today.Value.AddDays(30),
            recordedAt: PurchasingFixtures.Now);
}

public sealed class ThreeWayMatchTests
{
    [Fact]
    public void An_invoice_that_agrees_with_the_order_and_the_receipts_matches_exactly()
    {
        var order = MatchFixtures.SentOrder();
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Variances.ShouldBeEmpty();
        result.IsPayable.ShouldBeTrue();
    }

    [Fact]
    public void Quantity_is_checked_against_what_arrived_not_against_what_was_ordered()
    {
        // Ordered 100, only 60 arrived, supplier billed for the full 100. A two-way match
        // against the order alone would pay this without complaint. That is the entire
        // reason the third document exists.
        var order = MatchFixtures.SentOrder();
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 60m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.Blocked);
        var variance = result.Variances.ShouldHaveSingleItem();
        variance.Type.ShouldBe(MatchVarianceType.Quantity);
        variance.Billed.ShouldBe(100m);
        variance.Expected.ShouldBe(60m);
        variance.FinancialImpact.ShouldBe(MatchFixtures.M(400m));
    }

    [Fact]
    public void Price_is_checked_against_what_was_agreed_not_against_the_delivery_note()
    {
        // The goods arrived in full, but the delivery note carried a price of 12 and the
        // invoice followed it. The agreement was 10. Checking price against the receipt
        // would let a supplier reprice unilaterally by writing a new number on a docket.
        var order = MatchFixtures.SentOrder(unitPrice: 10m);
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m, unitPrice: 12m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(12m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.Blocked);
        var variance = result.Variances.ShouldHaveSingleItem();
        variance.Type.ShouldBe(MatchVarianceType.Price);
        variance.Expected.ShouldBe(10m);
        variance.FinancialImpact.ShouldBe(MatchFixtures.M(200m));
    }

    [Fact]
    public void Partial_receipts_are_summed_so_one_invoice_can_cover_several_deliveries()
    {
        var order = MatchFixtures.SentOrder();
        var first = MatchFixtures.PostedReceipt(order, "GRN-1", 40m);
        var second = MatchFixtures.PostedReceipt(order, "GRN-2", 60m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [first, second], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void Being_billed_for_goods_that_never_arrived_is_always_a_block_however_generous_the_tolerance()
    {
        var order = MatchFixtures.SentOrder();

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        // A tolerance wide enough to swallow the entire order.
        var generous = new MatchTolerance(100m, MatchFixtures.M(10_000m), 100m, 10_000m);

        var result = ThreeWayMatcher.Match(order, [], invoice, generous);

        result.Outcome.ShouldBe(MatchOutcome.Blocked);
        result.Variances.ShouldHaveSingleItem().Type.ShouldBe(MatchVarianceType.NothingReceived);
        result.IsPayable.ShouldBeFalse();
    }

    [Fact]
    public void A_small_overbilling_inside_tolerance_is_payable_but_is_reported_as_a_tolerance_pass_not_a_match()
    {
        // Billed 10.15 against an agreed 10.00 — 1.5%, inside the default 2%. Payable,
        // but a supplier permanently sitting just under the tolerance is a commercial
        // problem, and it is invisible if this is reported as an exact match.
        var order = MatchFixtures.SentOrder(unitPrice: 10m);
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10.15m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Default(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.MatchedWithinTolerance);
        result.IsPayable.ShouldBeTrue();
        result.Variances.ShouldBeEmpty();
    }

    [Fact]
    public void Tolerance_is_asymmetric_and_does_not_need_to_forgive_a_supplier_who_undercharged()
    {
        var order = MatchFixtures.SentOrder(unitPrice: 10m);
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(4m)).IsSuccess.ShouldBeTrue();

        // Half price, far outside any tolerance, yet still payable: being undercharged is
        // not a control failure.
        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp));

        result.IsPayable.ShouldBeTrue();
        result.Outcome.ShouldBe(MatchOutcome.MatchedWithinTolerance);
    }

    [Fact]
    public void An_invoice_in_a_different_currency_is_blocked_before_any_arithmetic_is_attempted()
    {
        var order = MatchFixtures.SentOrder();
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);

        var invoice = MatchFixtures.Invoice(order, currency: "EUR");
        invoice.AddLine(1, MatchFixtures.Widget, 100m, new Money(10m, "EUR")).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Default("EUR"));

        result.Outcome.ShouldBe(MatchOutcome.Blocked);
        result.Variances.ShouldHaveSingleItem().Type.ShouldBe(MatchVarianceType.Currency);
    }

    [Fact]
    public void An_invoice_line_referring_to_an_order_line_that_does_not_exist_is_blocked()
    {
        var order = MatchFixtures.SentOrder();
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);

        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();
        invoice.AddLine(9, MatchFixtures.Gadget, 5m, MatchFixtures.M(3m)).IsSuccess.ShouldBeTrue();

        var result = ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Default(PurchasingFixtures.Gbp));

        result.Outcome.ShouldBe(MatchOutcome.Blocked);
        result.Variances.ShouldContain(v => v.Type == MatchVarianceType.NoSuchOrderLine);
    }
}

public sealed class PurchaseInvoiceLifecycleTests
{
    [Fact]
    public void An_invoice_is_recorded_before_it_is_matched_so_a_disputed_bill_is_still_visible()
    {
        var order = MatchFixtures.SentOrder();
        var invoice = MatchFixtures.Invoice(order);

        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Recorded);
    }

    [Fact]
    public void A_blocked_invoice_cannot_be_approved_for_payment()
    {
        var order = MatchFixtures.SentOrder();
        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        invoice.ApplyMatch(ThreeWayMatcher.Match(order, [], invoice, MatchTolerance.Default(PurchasingFixtures.Gbp)));
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Blocked);

        var approved = invoice.Approve(Guid.CreateVersion7(), PurchasingFixtures.Now);

        approved.IsFailure.ShouldBeTrue();
        approved.Error.ShouldBe(PurchasingErrors.InvoiceNotMatched);
    }

    [Fact]
    public void A_block_records_why_so_the_buyer_is_not_left_guessing()
    {
        var order = MatchFixtures.SentOrder();
        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();

        invoice.ApplyMatch(ThreeWayMatcher.Match(order, [], invoice, MatchTolerance.Default(PurchasingFixtures.Gbp)));

        invoice.BlockReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Overriding_a_block_is_permitted_but_only_with_a_reason_and_a_named_person()
    {
        var order = MatchFixtures.SentOrder();
        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();
        invoice.ApplyMatch(ThreeWayMatcher.Match(order, [], invoice, MatchTolerance.Default(PurchasingFixtures.Gbp)));

        invoice.OverrideBlock(Guid.CreateVersion7(), "   ", PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.OverrideReasonRequired);

        var financeController = Guid.CreateVersion7();
        var overridden = invoice.OverrideBlock(financeController, "Goods confirmed received by store manager", PurchasingFixtures.Now);

        overridden.IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Approved);
        invoice.ApprovedByUserId.ShouldBe(financeController);
        invoice.BlockReason.ShouldNotBeNull();
        invoice.BlockReason!.ShouldStartWith("Overridden:");
    }

    [Fact]
    public void A_matched_invoice_can_be_approved_and_then_paid_but_not_paid_first()
    {
        var order = MatchFixtures.SentOrder();
        var receipt = MatchFixtures.PostedReceipt(order, "GRN-1", 100m);
        var invoice = MatchFixtures.Invoice(order);
        invoice.AddLine(1, MatchFixtures.Widget, 100m, MatchFixtures.M(10m)).IsSuccess.ShouldBeTrue();
        invoice.ApplyMatch(ThreeWayMatcher.Match(order, [receipt], invoice, MatchTolerance.Strict(PurchasingFixtures.Gbp)));

        invoice.MarkPaid().Error.ShouldBe(PurchasingErrors.InvoiceNotApproved);

        invoice.Approve(Guid.CreateVersion7(), PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        invoice.MarkPaid().IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Paid);
    }
}
