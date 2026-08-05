using POS.Purchasing.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

internal static class PurchasingFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
    public static readonly BusinessDate Today = BusinessDate.Open(new DateOnly(2026, 7, 22));
    public const string Gbp = "GBP";

    public static Money M(decimal amount) => new(amount, Gbp);

    public static Supplier Supplier(SupplierTerms? terms = null) =>
        Purchasing.Domain.Supplier.Create(
            tenantId: Guid.CreateVersion7(),
            companyId: Guid.CreateVersion7(),
            code: "ACME",
            name: "Acme Wholesale",
            currency: Gbp,
            terms: terms ?? SupplierTerms.Default);

    public static PurchaseOrder Order(Supplier supplier, Guid raisedBy) =>
        PurchaseOrder.Raise(
            tenantId: supplier.TenantId,
            companyId: supplier.CompanyId,
            branchId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            supplier: supplier,
            orderNumber: "PO-0001",
            raisedByUserId: raisedBy,
            raisedAt: Now,
            businessDate: Today);

    public static ApprovalPolicy Policy(decimal above = 100m) =>
        new(M(above), [new ApprovalThreshold(M(1000m), ApprovalLevel.Manager),
                       new ApprovalThreshold(M(10000m), ApprovalLevel.Director)]);

    public static GoodsReceipt Receipt(PurchaseOrder order, string number) =>
        GoodsReceipt.Create(
            tenantId: order.TenantId,
            branchId: order.BranchId,
            warehouseId: order.WarehouseId,
            purchaseOrderId: order.Id,
            supplierId: order.SupplierId,
            receiptNumber: number,
            currency: Gbp,
            supplierDeliveryNote: "DN-77",
            receivedByUserId: Guid.CreateVersion7(),
            receivedAt: Now,
            businessDate: Today);
}

public sealed class SupplierTests
{
    [Fact]
    public void A_supplier_code_is_normalised_so_lookups_are_predictable()
    {
        var supplier = Supplier.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "  acme  ", "Acme", "GBP", SupplierTerms.Default);

        supplier.Code.ShouldBe("ACME");
    }

    [Fact]
    public void A_variant_can_only_have_one_code_per_supplier()
    {
        var supplier = PurchasingFixtures.Supplier();
        var variant = Guid.CreateVersion7();

        supplier.AddProductCode(variant, "AC-100", packSize: 24m).IsSuccess.ShouldBeTrue();
        var second = supplier.AddProductCode(variant, "AC-101", packSize: 12m);

        second.IsFailure.ShouldBeTrue();
        second.Error.ShouldBe(PurchasingErrors.DuplicateSupplierProductCode);
    }

    [Fact]
    public void One_supplier_code_may_cover_several_of_our_variants()
    {
        var supplier = PurchasingFixtures.Supplier();

        supplier.AddProductCode(Guid.CreateVersion7(), "PACK-A", 6m).IsSuccess.ShouldBeTrue();
        supplier.AddProductCode(Guid.CreateVersion7(), "PACK-A", 6m).IsSuccess.ShouldBeTrue();

        supplier.ProductCodes.Count.ShouldBe(2);
    }

    [Fact]
    public void Pack_size_must_be_positive_because_ordering_zero_of_something_is_meaningless()
    {
        var supplier = PurchasingFixtures.Supplier();

        supplier.AddProductCode(Guid.CreateVersion7(), "AC-1", packSize: 0m)
            .Error.ShouldBe(PurchasingErrors.PackSizeMustBePositive);
    }

    [Fact]
    public void A_supplier_is_deactivated_never_deleted()
    {
        var supplier = PurchasingFixtures.Supplier();

        supplier.Deactivate();
        supplier.IsActive.ShouldBeFalse();

        supplier.Reactivate();
        supplier.IsActive.ShouldBeTrue();
    }
}

public sealed class PurchaseOrderApprovalTests
{
    [Fact]
    public void An_order_below_the_threshold_is_approved_on_submission()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 2m, PurchasingFixtures.M(10m));

        order.Submit(PurchasingFixtures.Policy(above: 100m), PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(PurchaseOrderStatus.Approved);
    }

    [Fact]
    public void An_order_above_the_threshold_waits_for_a_human()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 100m, PurchasingFixtures.M(10m));

        order.Submit(PurchasingFixtures.Policy(above: 100m), PurchasingFixtures.Now);

        order.Status.ShouldBe(PurchaseOrderStatus.PendingApproval);
    }

    [Fact]
    public void The_buyer_cannot_approve_their_own_order()
    {
        var buyer = Guid.CreateVersion7();
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, buyer);
        order.AddLine(Guid.CreateVersion7(), 100m, PurchasingFixtures.M(10m));
        order.Submit(PurchasingFixtures.Policy(), PurchasingFixtures.Now);

        var result = order.Approve(PurchasingFixtures.Policy(), buyer, ApprovalLevel.Director, PurchasingFixtures.Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(PurchasingErrors.SelfApprovalForbidden);
        order.Status.ShouldBe(PurchaseOrderStatus.PendingApproval);
    }

    [Fact]
    public void Self_approval_is_allowed_only_when_the_tenant_has_explicitly_permitted_it()
    {
        var owner = Guid.CreateVersion7();
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, owner);
        order.AddLine(Guid.CreateVersion7(), 100m, PurchasingFixtures.M(10m));

        var policy = new ApprovalPolicy(PurchasingFixtures.M(0m), [], allowSelfApproval: true);
        order.Submit(policy, PurchasingFixtures.Now);

        order.Approve(policy, owner, ApprovalLevel.Manager, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Approved);
    }

    [Fact]
    public void A_supervisor_cannot_approve_an_order_that_needs_a_director()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 2000m, PurchasingFixtures.M(10m)); // 20,000
        var policy = PurchasingFixtures.Policy();
        order.Submit(policy, PurchasingFixtures.Now);

        policy.RequiredLevel(order.TotalValue).ShouldBe(ApprovalLevel.Director);

        order.Approve(policy, Guid.CreateVersion7(), ApprovalLevel.Supervisor, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.ApprovalLevelInsufficient);
    }

    [Fact]
    public void A_more_senior_approver_satisfies_a_lower_requirement()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 200m, PurchasingFixtures.M(10m)); // 2,000 -> Manager
        var policy = PurchasingFixtures.Policy();
        order.Submit(policy, PurchasingFixtures.Now);

        order.Approve(policy, Guid.CreateVersion7(), ApprovalLevel.Director, PurchasingFixtures.Now)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_rejected_order_becomes_editable_again_rather_than_dying()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 100m, PurchasingFixtures.M(10m));
        order.Submit(PurchasingFixtures.Policy(), PurchasingFixtures.Now);

        order.Reject(Guid.CreateVersion7(), ApprovalLevel.Manager, "Too many", PurchasingFixtures.Now)
            .IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(PurchaseOrderStatus.Rejected);
        order.IsEditable.ShouldBeTrue();
        order.Approvals.Single().Approved.ShouldBeFalse();
    }

    [Fact]
    public void Rejection_requires_a_reason()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 100m, PurchasingFixtures.M(10m));
        order.Submit(PurchasingFixtures.Policy(), PurchasingFixtures.Now);

        order.Reject(Guid.CreateVersion7(), ApprovalLevel.Manager, "  ", PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.RejectionReasonRequired);
    }

    [Fact]
    public void An_empty_order_cannot_be_submitted()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());

        order.Submit(PurchasingFixtures.Policy(), PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.OrderHasNoLines);
    }

    [Fact]
    public void An_approved_order_can_no_longer_be_edited()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        order.AddLine(Guid.CreateVersion7(), 2m, PurchasingFixtures.M(10m));
        order.Submit(PurchasingFixtures.Policy(), PurchasingFixtures.Now);

        order.AddLine(Guid.CreateVersion7(), 1m, PurchasingFixtures.M(5m))
            .Error.ShouldBe(PurchasingErrors.OrderNotEditable);
    }

    [Fact]
    public void The_same_variant_cannot_appear_twice_on_one_order()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        var variant = Guid.CreateVersion7();

        order.AddLine(variant, 2m, PurchasingFixtures.M(10m)).IsSuccess.ShouldBeTrue();
        order.AddLine(variant, 3m, PurchasingFixtures.M(10m))
            .Error.ShouldBe(PurchasingErrors.DuplicateOrderLine);
    }

    [Fact]
    public void An_order_inherits_the_suppliers_currency_and_rejects_any_other()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());

        order.Currency.ShouldBe("GBP");
        order.AddLine(Guid.CreateVersion7(), 1m, new Money(10m, "EUR"))
            .Error.ShouldBe(PurchasingErrors.CurrencyMismatch);
    }

    [Fact]
    public void Terms_are_snapshotted_so_renegotiating_does_not_restate_old_orders()
    {
        var supplier = PurchasingFixtures.Supplier(new SupplierTerms(paymentTermDays: 30, leadTimeDays: 7));
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());

        supplier.UpdateTerms(new SupplierTerms(paymentTermDays: 60, leadTimeDays: 21));

        order.AgreedTerms.PaymentTermDays.ShouldBe(30);
        order.AgreedTerms.LeadTimeDays.ShouldBe(7);
        order.ExpectedDeliveryDate.ShouldBe(new DateOnly(2026, 7, 29));
    }
}

public sealed class PartialReceiptTests
{
    private static (PurchaseOrder Order, Guid Variant) SentOrder(decimal quantity, decimal price)
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        var variant = Guid.CreateVersion7();
        order.AddLine(variant, quantity, PurchasingFixtures.M(price));
        order.Submit(ApprovalPolicy.None(PurchasingFixtures.Gbp), PurchasingFixtures.Now);
        order.Send(PurchasingFixtures.Now);
        return (order, variant);
    }

    [Fact]
    public void Receiving_47_of_50_leaves_3_outstanding_not_a_closed_line()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 47m, PurchasingFixtures.M(10m));

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        var line = order.Lines.Single();
        line.QuantityReceived.ShouldBe(47m);
        line.OutstandingQuantity.ShouldBe(3m);
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.IsFullyResolved.ShouldBeFalse();
    }

    [Fact]
    public void The_remaining_3_arriving_later_completes_the_order()
    {
        var (order, variant) = SentOrder(50m, 10m);

        var first = PurchasingFixtures.Receipt(order, "GRN-1");
        first.AddLine(1, variant, 47m, PurchasingFixtures.M(10m));
        first.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);

        var second = PurchasingFixtures.Receipt(order, "GRN-2");
        second.AddLine(1, variant, 3m, PurchasingFixtures.M(10m));
        second.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);

        order.Lines.Single().OutstandingQuantity.ShouldBe(0m);
        order.Status.ShouldBe(PurchaseOrderStatus.Received);
    }

    [Fact]
    public void An_over_receipt_within_tolerance_is_accepted_and_reported()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 52m, PurchasingFixtures.M(10m));

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        var line = order.Lines.Single();
        line.OverReceivedQuantity.ShouldBe(2m);
        // Outstanding never goes negative: an over-receipt is not a debt the supplier owes us.
        line.OutstandingQuantity.ShouldBe(0m);
    }

    [Fact]
    public void An_over_receipt_beyond_tolerance_is_refused()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 80m, PurchasingFixtures.M(10m));

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.OverReceiptExceedsTolerance);
    }

    [Fact]
    public void A_refused_posting_leaves_the_order_completely_untouched()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 10m, PurchasingFixtures.M(10m));
        receipt.AddLine(1, variant, 500m, PurchasingFixtures.M(10m)); // blows the tolerance

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now).IsFailure.ShouldBeTrue();

        // The valid first line must not have been applied. A half-posted receipt is a
        // stock count nobody can explain.
        order.Lines.Single().QuantityReceived.ShouldBe(0m);
        order.Status.ShouldBe(PurchaseOrderStatus.Sent);
        receipt.IsPosted.ShouldBeFalse();
    }

    [Fact]
    public void Tolerance_accepts_whichever_of_percentage_or_absolute_is_kinder()
    {
        var tolerance = new ReceiptTolerance(percentage: 2m, absoluteUnits: 5m);

        // Small order: the absolute allowance is the generous one.
        tolerance.PermitsOverReceipt(ordered: 3m, receivedTotal: 8m).ShouldBeTrue();
        tolerance.PermitsOverReceipt(ordered: 3m, receivedTotal: 9m).ShouldBeFalse();

        // Large order: the percentage allowance is the generous one.
        tolerance.PermitsOverReceipt(ordered: 10_000m, receivedTotal: 10_150m).ShouldBeTrue();
        tolerance.PermitsOverReceipt(ordered: 10_000m, receivedTotal: 10_300m).ShouldBeFalse();
    }

    [Fact]
    public void Short_shipment_closure_is_an_explicit_act_not_a_side_effect_of_receiving()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 47m, PurchasingFixtures.M(10m));
        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);

        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);

        order.CancelOutstanding(1, "Supplier discontinued the line").IsSuccess.ShouldBeTrue();

        order.Lines.Single().QuantityCancelled.ShouldBe(3m);
        order.Status.ShouldBe(PurchaseOrderStatus.Received);
    }

    [Fact]
    public void Cancelling_outstanding_requires_a_reason()
    {
        var (order, _) = SentOrder(50m, 10m);

        order.CancelOutstanding(1, "")
            .Error.ShouldBe(PurchasingErrors.CancellationReasonRequired);
    }

    [Fact]
    public void An_order_with_goods_already_received_cannot_be_cancelled_wholesale()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 10m, PurchasingFixtures.M(10m));
        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);

        order.Cancel("Changed our minds", PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.CannotCancelPartiallyReceivedOrder);
    }

    [Fact]
    public void Goods_cannot_be_received_against_an_order_that_was_never_sent()
    {
        var supplier = PurchasingFixtures.Supplier();
        var order = PurchasingFixtures.Order(supplier, Guid.CreateVersion7());
        var variant = Guid.CreateVersion7();
        order.AddLine(variant, 10m, PurchasingFixtures.M(10m));

        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 10m, PurchasingFixtures.M(10m));

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.OrderNotReceivable);
    }

    [Fact]
    public void A_receipt_cannot_be_posted_twice()
    {
        var (order, variant) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, variant, 10m, PurchasingFixtures.M(10m));
        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.ReceiptAlreadyPosted);
    }

    [Fact]
    public void A_receipt_for_a_different_variant_than_the_order_line_is_refused()
    {
        var (order, _) = SentOrder(50m, 10m);
        var receipt = PurchasingFixtures.Receipt(order, "GRN-1");
        receipt.AddLine(1, Guid.CreateVersion7(), 10m, PurchasingFixtures.M(10m));

        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.ReceiptLineVariantMismatch);
    }

    [Fact]
    public void A_receipt_belonging_to_another_order_is_refused()
    {
        var (orderA, variant) = SentOrder(50m, 10m);
        var (orderB, _) = SentOrder(50m, 10m);

        var receipt = PurchasingFixtures.Receipt(orderA, "GRN-1");
        receipt.AddLine(1, variant, 10m, PurchasingFixtures.M(10m));

        receipt.Post(orderB, ReceiptTolerance.Default, PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.ReceiptOrderMismatch);
    }
}

public sealed class SupplierReturnTests
{
    private static SupplierReturn Return()
    {
        return SupplierReturn.Create(
            tenantId: Guid.CreateVersion7(),
            branchId: Guid.CreateVersion7(),
            warehouseId: Guid.CreateVersion7(),
            supplierId: Guid.CreateVersion7(),
            returnNumber: "SR-1",
            currency: PurchasingFixtures.Gbp,
            reason: SupplierReturnReason.Damaged,
            raisedByUserId: Guid.CreateVersion7(),
            raisedAt: PurchasingFixtures.Now,
            businessDate: PurchasingFixtures.Today);
    }

    [Fact]
    public void Dispatching_produces_outbound_instructions_without_touching_inventory_types()
    {
        var supplierReturn = Return();
        supplierReturn.AddLine(Guid.CreateVersion7(), 5m, PurchasingFixtures.M(4m));

        var posting = supplierReturn.Dispatch(PurchasingFixtures.Now);

        posting.IsSuccess.ShouldBeTrue();
        posting.Value.Movements.Single().Quantity.ShouldBe(5m);
        supplierReturn.Status.ShouldBe(SupplierReturnStatus.Dispatched);
    }

    [Fact]
    public void An_empty_return_cannot_be_dispatched()
    {
        Return().Dispatch(PurchasingFixtures.Now)
            .Error.ShouldBe(PurchasingErrors.ReturnHasNoLines);
    }

    [Fact]
    public void A_credit_note_cannot_be_recorded_before_the_goods_have_left()
    {
        var supplierReturn = Return();
        supplierReturn.AddLine(Guid.CreateVersion7(), 5m, PurchasingFixtures.M(4m));

        supplierReturn.RecordCreditNote("CN-1", PurchasingFixtures.M(20m), new DateOnly(2026, 8, 1))
            .Error.ShouldBe(PurchasingErrors.ReturnNotAwaitingCredit);
    }

    [Fact]
    public void A_full_credit_closes_the_return()
    {
        var supplierReturn = Return();
        supplierReturn.AddLine(Guid.CreateVersion7(), 5m, PurchasingFixtures.M(4m));
        supplierReturn.Dispatch(PurchasingFixtures.Now);

        supplierReturn.RecordCreditNote("CN-1", PurchasingFixtures.M(20m), new DateOnly(2026, 8, 1))
            .IsSuccess.ShouldBeTrue();

        supplierReturn.Status.ShouldBe(SupplierReturnStatus.Credited);
        supplierReturn.CreditShortfall.ShouldBe(PurchasingFixtures.M(0m));
    }

    [Fact]
    public void A_short_credit_leaves_the_disagreement_visible_rather_than_erasing_it()
    {
        var supplierReturn = Return();
        supplierReturn.AddLine(Guid.CreateVersion7(), 5m, PurchasingFixtures.M(4m));
        supplierReturn.Dispatch(PurchasingFixtures.Now);

        supplierReturn.RecordCreditNote("CN-1", PurchasingFixtures.M(15m), new DateOnly(2026, 8, 1));

        supplierReturn.Status.ShouldBe(SupplierReturnStatus.PartiallyCredited);
        supplierReturn.ExpectedCredit.ShouldBe(PurchasingFixtures.M(20m));
        supplierReturn.CreditedAmount.ShouldBe(PurchasingFixtures.M(15m));
        supplierReturn.CreditShortfall.ShouldBe(PurchasingFixtures.M(5m));
    }

    [Fact]
    public void A_dispatched_return_cannot_be_cancelled_because_the_goods_have_gone()
    {
        var supplierReturn = Return();
        supplierReturn.AddLine(Guid.CreateVersion7(), 5m, PurchasingFixtures.M(4m));
        supplierReturn.Dispatch(PurchasingFixtures.Now);

        supplierReturn.Cancel().Error.ShouldBe(PurchasingErrors.ReturnNotEditable);
    }
}
