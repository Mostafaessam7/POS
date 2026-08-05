using POS.Inventory.Domain;
using POS.Purchasing.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// The Phase 7 gate, exercised end to end: an order is raised and approved, received in
/// two partial deliveries each carrying its own freight, the supplier's invoice is matched
/// with a tolerance variance, and the stock balance is checked at every step.
/// </summary>
/// <remarks>
/// This is the test that would catch a landed cost quietly failing to reach inventory.
/// Each individual rule is covered elsewhere; the value here is in the arithmetic
/// surviving the whole chain, because that is where cost accounting actually goes wrong.
///
/// Purchasing produces plain instructions and this test applies them to Inventory's
/// aggregates, exactly as the application layer will. Neither module references the
/// other's types (ADR 002).
/// </remarks>
public sealed class PurchasingToInventoryWorkflowTests
{
    private static readonly Guid Widget = Guid.CreateVersion7();

    private static Money M(decimal amount) => new(amount, PurchasingFixtures.Gbp);

    [Fact]
    public void An_order_received_in_two_deliveries_with_freight_lands_the_right_weighted_average_cost()
    {
        // ── Raise, approve, send ────────────────────────────────────────────────────
        var supplier = PurchasingFixtures.Supplier();
        var buyer = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();

        var order = PurchasingFixtures.Order(supplier, buyer);
        order.AddLine(Widget, 100m, M(10m)).IsSuccess.ShouldBeTrue();

        var policy = PurchasingFixtures.Policy();
        order.Submit(policy, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.PendingApproval);

        order.Approve(policy, manager, POS.Purchasing.Domain.ApprovalLevel.Manager, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();
        order.Send(PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        var balance = StockBalance.Empty(order.TenantId, order.WarehouseId, Widget, PurchasingFixtures.Gbp);
        var ledger = new List<StockMovement>();

        // ── First delivery: 60 units, 60.00 of freight ──────────────────────────────
        // Goods 600.00 + freight 60.00 = 660.00 over 60 units = 11.00 landed.
        var first = PurchasingFixtures.Receipt(order, "GRN-0001");
        first.AddLine(1, Widget, 60m, M(10m)).IsSuccess.ShouldBeTrue();
        first.AddLandedCost(LandedCostType.Freight, M(60m), "HAULIER-881", LandedCostAllocationBasis.Quantity)
            .IsSuccess.ShouldBeTrue();

        var firstPosting = first.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);
        firstPosting.IsSuccess.ShouldBeTrue();

        var firstInstruction = firstPosting.Value.Movements.ShouldHaveSingleItem();
        firstInstruction.LandedUnitCost.ShouldBe(M(11m));
        firstInstruction.AllocatedLandedCost.ShouldBe(M(60m));

        Apply(balance, ledger, order, firstPosting.Value, firstInstruction);

        balance.QuantityOnHand.ShouldBe(60m);
        balance.AverageUnitCost.ShouldBe(M(11m));
        balance.TotalValue.ShouldBe(M(660m));

        // The order knows it is part-way through, and by how much.
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        var line = order.Lines.ShouldHaveSingleItem();
        line.QuantityReceived.ShouldBe(60m);
        line.OutstandingQuantity.ShouldBe(40m);

        // ── Second delivery: 40 units, 20.00 of freight ─────────────────────────────
        // Goods 400.00 + freight 20.00 = 420.00 over 40 units = 10.50 landed.
        var second = PurchasingFixtures.Receipt(order, "GRN-0002");
        second.AddLine(1, Widget, 40m, M(10m)).IsSuccess.ShouldBeTrue();
        second.AddLandedCost(LandedCostType.Freight, M(20m), "HAULIER-905", LandedCostAllocationBasis.Quantity)
            .IsSuccess.ShouldBeTrue();

        var secondPosting = second.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);
        secondPosting.IsSuccess.ShouldBeTrue();

        var secondInstruction = secondPosting.Value.Movements.ShouldHaveSingleItem();
        secondInstruction.LandedUnitCost.ShouldBe(M(10.50m));

        Apply(balance, ledger, order, secondPosting.Value, secondInstruction);

        // Weighted average across two deliveries at different landed costs:
        // (660.00 + 420.00) / 100 = 10.80. Not 10.00, which is what the invoice says,
        // and not 11.00, which is what the first delivery cost.
        balance.QuantityOnHand.ShouldBe(100m);
        balance.TotalValue.ShouldBe(M(1080m));
        balance.AverageUnitCost.ShouldBe(M(10.80m));

        order.Status.ShouldBe(PurchaseOrderStatus.Received);
        order.Lines[0].OutstandingQuantity.ShouldBe(0m);

        // The ledger carries both receipts, and its value agrees with the balance.
        ledger.Count.ShouldBe(2);
        ledger.Aggregate(Money.Zero(PurchasingFixtures.Gbp), (sum, m) => sum + m.TotalCost)
            .ShouldBe(balance.TotalValue);

        // ── The supplier's invoice, 1.5% high on price ──────────────────────────────
        var invoice = PurchaseInvoice.Record(
            tenantId: order.TenantId,
            companyId: order.CompanyId,
            supplierId: order.SupplierId,
            purchaseOrderId: order.Id,
            supplierInvoiceNumber: "SI-9910",
            currency: PurchasingFixtures.Gbp,
            invoiceDate: PurchasingFixtures.Today.Value,
            dueDate: PurchasingFixtures.Today.Value.AddDays(supplier.Terms.PaymentTermDays),
            recordedAt: PurchasingFixtures.Now);

        invoice.AddLine(1, Widget, 100m, M(10.15m)).IsSuccess.ShouldBeTrue();

        var match = ThreeWayMatcher.Match(
            order,
            [first, second],
            invoice,
            MatchTolerance.Default(PurchasingFixtures.Gbp));

        // Quantity agrees with the two receipts summed; price is 1.5% over the agreed
        // 10.00, inside the 2% tolerance. Payable, but flagged as a tolerance pass.
        match.Outcome.ShouldBe(MatchOutcome.MatchedWithinTolerance);
        match.IsPayable.ShouldBeTrue();

        invoice.ApplyMatch(match);
        invoice.Status.ShouldBe(PurchaseInvoiceStatus.Matched);
        invoice.Approve(Guid.CreateVersion7(), PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        // Note what did *not* happen: the invoice price never touched the stock balance.
        // Cost comes from the receipt, not from the bill.
        balance.AverageUnitCost.ShouldBe(M(10.80m));
    }

    [Fact]
    public void A_freight_invoice_that_arrives_after_half_the_stock_is_sold_revalues_what_is_left_and_expenses_the_rest()
    {
        var order = MatchFixtures.SentOrder();
        var balance = StockBalance.Empty(order.TenantId, order.WarehouseId, MatchFixtures.Widget, PurchasingFixtures.Gbp);

        // 100 units land at a clean 10.00 — no freight known yet.
        var receipt = PurchasingFixtures.Receipt(order, "GRN-0001");
        receipt.AddLine(1, MatchFixtures.Widget, 100m, M(10m)).IsSuccess.ShouldBeTrue();
        var posting = receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now);
        posting.IsSuccess.ShouldBeTrue();

        var instruction = posting.Value.Movements.ShouldHaveSingleItem();
        instruction.LandedUnitCost.ShouldBe(M(10m));
        balance.ApplyInbound(instruction.Quantity, instruction.LandedUnitCost, PurchasingFixtures.Now);

        // Half of them sell before the haulier gets round to invoicing.
        balance.ApplyOutbound(50m, PurchasingFixtures.Now);
        balance.QuantityOnHand.ShouldBe(50m);
        balance.TotalValue.ShouldBe(M(500m));

        // Freight of 100.00 turns up three weeks late.
        var split = LateLandedCostAllocator.Split(M(100m), quantityReceived: 100m, quantityStillOnHand: balance.QuantityOnHand);

        split.Revaluation.ShouldBe(M(50m));
        split.Variance.ShouldBe(M(50m));

        // The half attributable to stock still held revalues it; nothing else may.
        var adjustment = StockMovement.RecordValueAdjustment(
            tenantId: order.TenantId,
            warehouseId: order.WarehouseId,
            variantId: MatchFixtures.Widget,
            valueDelta: split.Revaluation,
            reference: new StockDocumentReference(StockDocumentType.LandedCost, receipt.Id, receipt.ReceiptNumber),
            occurredAt: PurchasingFixtures.Now,
            businessDate: PurchasingFixtures.Today.Value,
            userId: Guid.CreateVersion7(),
            reasonCode: "LATE_LANDED_COST");

        adjustment.QuantityDelta.ShouldBe(0m);
        adjustment.TotalCost.ShouldBe(M(50m));
        adjustment.Type.ShouldBe(MovementType.CostAdjustment);

        balance.ApplyValueAdjustment(split.Revaluation, PurchasingFixtures.Now);

        // 50 units now carry 550.00 — 11.00 each. The other 50.00 belongs to goods that
        // have already gone out of the door and cannot be put back into stock value.
        balance.QuantityOnHand.ShouldBe(50m);
        balance.TotalValue.ShouldBe(M(550m));
        balance.AverageUnitCost.ShouldBe(M(11m));
    }

    [Fact]
    public void A_short_shipment_is_closed_explicitly_rather_than_left_open_forever()
    {
        var order = MatchFixtures.SentOrder();

        var receipt = PurchasingFixtures.Receipt(order, "GRN-0001");
        receipt.AddLine(1, MatchFixtures.Widget, 90m, M(10m)).IsSuccess.ShouldBeTrue();
        receipt.Post(order, ReceiptTolerance.Default, PurchasingFixtures.Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        order.Lines[0].OutstandingQuantity.ShouldBe(10m);

        // The supplier says the last ten are not coming. Somebody has to say so; the
        // order does not quietly decide it is finished.
        order.CancelOutstanding(1, "Supplier discontinued the line").IsSuccess.ShouldBeTrue();

        order.Lines[0].OutstandingQuantity.ShouldBe(0m);
        order.Lines[0].QuantityCancelled.ShouldBe(10m);
        order.Status.ShouldBe(PurchaseOrderStatus.Received);
    }

    private static void Apply(
        StockBalance balance,
        List<StockMovement> ledger,
        PurchaseOrder order,
        GoodsReceiptPosting posting,
        StockReceiptInstruction instruction)
    {
        ledger.Add(StockMovement.Record(
            tenantId: order.TenantId,
            warehouseId: posting.WarehouseId,
            variantId: instruction.VariantId,
            type: MovementType.Receipt,
            quantityDelta: instruction.Quantity,
            unitCost: instruction.LandedUnitCost,
            reference: new StockDocumentReference(StockDocumentType.PurchaseReceipt, posting.GoodsReceiptId, posting.ReceiptNumber),
            occurredAt: posting.PostedAt,
            businessDate: posting.BusinessDate.Value,
            terminalId: null,
            userId: null));

        balance.ApplyInbound(instruction.Quantity, instruction.LandedUnitCost, posting.PostedAt);
    }
}
