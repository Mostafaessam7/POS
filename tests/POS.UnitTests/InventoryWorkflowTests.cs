using POS.Inventory.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class StockTransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

    private static StockTransfer Draft() => StockTransfer.Draft(
        tenantId: Guid.CreateVersion7(),
        sourceWarehouseId: Guid.CreateVersion7(),
        destinationWarehouseId: Guid.CreateVersion7(),
        inTransitWarehouseId: Guid.CreateVersion7(),
        transferNumber: "TR-0001",
        createdByUserId: Guid.CreateVersion7(),
        now: Now);

    [Fact]
    public void A_transfer_to_the_same_warehouse_is_rejected()
    {
        var id = Guid.CreateVersion7();

        Should.Throw<ArgumentException>(() => StockTransfer.Draft(
            Guid.CreateVersion7(), id, id, Guid.CreateVersion7(), "TR-1", Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void An_empty_transfer_cannot_be_dispatched()
    {
        Draft().Dispatch(Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Adding_the_same_variant_twice_merges_the_lines()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();

        transfer.AddLine(variantId, 5m);
        transfer.AddLine(variantId, 3m);

        transfer.Lines.Count.ShouldBe(1);
        transfer.Lines[0].QuantitySent.ShouldBe(8m);
    }

    [Fact]
    public void A_fully_received_transfer_completes()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        transfer.Receive(
            new Dictionary<Guid, decimal> { [variantId] = 10m },
            Guid.CreateVersion7(),
            Now.AddDays(3));

        transfer.HasVariance.ShouldBeFalse();
        transfer.Status.ShouldBe(TransferStatus.Completed);
    }

    [Fact]
    public void A_short_receipt_leaves_the_transfer_in_variance()
    {
        // Ten sent, nine arrived. The missing unit must not evaporate — transfer
        // shrinkage is a known theft vector and the control is that a named person has
        // to decide it is gone.
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        transfer.Receive(
            new Dictionary<Guid, decimal> { [variantId] = 9m },
            Guid.CreateVersion7(),
            Now.AddDays(3));

        transfer.HasVariance.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.ReceivedWithVariance);
        transfer.Lines[0].Variance.ShouldBe(-1m);
    }

    [Fact]
    public void A_variance_cannot_be_written_off_without_a_reason()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);
        transfer.Receive(new Dictionary<Guid, decimal> { [variantId] = 9m }, Guid.CreateVersion7(), Now);

        transfer.WriteOffVariance(
            VarianceApprovalPolicy.None(), Money.Zero("USD"), Guid.CreateVersion7(), ApprovalLevel.Director, "", Now)
            .IsFailure.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.ReceivedWithVariance);
    }

    [Fact]
    public void Writing_off_a_variance_completes_the_transfer_and_records_who_decided()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);
        transfer.Receive(new Dictionary<Guid, decimal> { [variantId] = 9m }, Guid.CreateVersion7(), Now);

        transfer.WriteOffVariance(
            VarianceApprovalPolicy.None(), Money.Zero("USD"), manager, ApprovalLevel.Supervisor, "DAMAGED_IN_TRANSIT", Now)
            .IsSuccess.ShouldBeTrue();

        transfer.Status.ShouldBe(TransferStatus.Completed);
        transfer.VarianceWrittenOffByUserId.ShouldBe(manager);
        transfer.VarianceWriteOffReason.ShouldBe("DAMAGED_IN_TRANSIT");
    }

    [Fact]
    public void A_surplus_receipt_can_also_be_written_off()
    {
        // The aggregate itself is sign-agnostic: WriteOffVariance takes whatever value
        // the caller computed and does not care whether the underlying variance is a
        // shortfall or a surplus — only StockTransferService (§6 item 9) picks the
        // ledger movement type based on the sign. This pins down the aggregate's half
        // of that split: a surplus resolves exactly like a shortfall from here.
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        // 11 arrive against 10 sent.
        transfer.Receive(new Dictionary<Guid, decimal> { [variantId] = 11m }, Guid.CreateVersion7(), Now);

        transfer.HasVariance.ShouldBeTrue();
        transfer.Lines[0].Variance.ShouldBe(1m);

        transfer.WriteOffVariance(
            VarianceApprovalPolicy.None(), Money.Zero("USD"), manager, ApprovalLevel.Supervisor, "FOUND_STOCK", Now)
            .IsSuccess.ShouldBeTrue();

        transfer.Status.ShouldBe(TransferStatus.Completed);
        transfer.VarianceWrittenOffByUserId.ShouldBe(manager);
        transfer.VarianceWriteOffReason.ShouldBe("FOUND_STOCK");
    }

    [Fact]
    public void The_person_who_received_the_transfer_cannot_write_off_its_own_variance()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        var receiver = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);
        transfer.Receive(new Dictionary<Guid, decimal> { [variantId] = 9m }, receiver, Now);

        var policy = new VarianceApprovalPolicy([], allowSelfApproval: false);

        var result = transfer.WriteOffVariance(
            policy, Money.Zero("USD"), receiver, ApprovalLevel.Director, "DAMAGE", Now);

        result.IsFailure.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.ReceivedWithVariance);
    }

    [Fact]
    public void A_high_value_variance_requires_a_more_senior_approver()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);
        transfer.Receive(new Dictionary<Guid, decimal> { [variantId] = 9m }, Guid.CreateVersion7(), Now);

        var policy = new VarianceApprovalPolicy(
            [new VarianceApprovalThreshold(new Money(100m, "USD"), ApprovalLevel.Director)]);

        var result = transfer.WriteOffVariance(
            policy, new Money(500m, "USD"), Guid.CreateVersion7(), ApprovalLevel.Supervisor, "DAMAGE", Now);

        result.IsFailure.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.ReceivedWithVariance);
    }

    [Fact]
    public void A_line_missing_entirely_from_the_receipt_counts_as_nothing_arrived()
    {
        var transfer = Draft();
        var variantId = Guid.CreateVersion7();
        transfer.AddLine(variantId, 10m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        transfer.Receive(new Dictionary<Guid, decimal>(), Guid.CreateVersion7(), Now);

        transfer.Lines[0].Variance.ShouldBe(-10m);
        transfer.HasVariance.ShouldBeTrue();
    }

    [Fact]
    public void Lines_cannot_be_added_after_dispatch()
    {
        var transfer = Draft();
        transfer.AddLine(Guid.CreateVersion7(), 1m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        Should.Throw<InvalidOperationException>(() => transfer.AddLine(Guid.CreateVersion7(), 1m));
    }

    [Fact]
    public void A_draft_transfer_can_be_cancelled()
    {
        var transfer = Draft();
        var canceller = Guid.CreateVersion7();

        var result = transfer.Cancel("Wrong destination", canceller, Now);

        result.IsSuccess.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.Cancelled);
        transfer.CancelledByUserId.ShouldBe(canceller);
        transfer.CancellationReason.ShouldBe("Wrong destination");
    }

    [Fact]
    public void Cancelling_requires_a_reason()
    {
        var transfer = Draft();

        var result = transfer.Cancel("", Guid.CreateVersion7(), Now);

        result.IsFailure.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.Draft);
    }

    [Fact]
    public void A_dispatched_transfer_cannot_be_cancelled()
    {
        // Stock has already left the source warehouse by this point — cancelling would
        // need to reverse a real movement, not just flip a status.
        var transfer = Draft();
        transfer.AddLine(Guid.CreateVersion7(), 1m);
        transfer.Dispatch(Guid.CreateVersion7(), Now);

        var result = transfer.Cancel("Changed my mind", Guid.CreateVersion7(), Now);

        result.IsFailure.ShouldBeTrue();
        transfer.Status.ShouldBe(TransferStatus.InTransit);
    }
}

public sealed class StocktakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 7, 20);

    private static Stocktake Start(bool blind = true) => Stocktake.Start(
        tenantId: Guid.CreateVersion7(),
        warehouseId: Guid.CreateVersion7(),
        stocktakeNumber: "ST-0001",
        scope: StocktakeScope.Full,
        isBlind: blind,
        startedByUserId: Guid.CreateVersion7(),
        now: Now,
        businessDate: BusinessDate);

    [Fact]
    public void A_count_records_the_variance_rather_than_overwriting_the_balance()
    {
        // Counting 5 where the system says 7 must yield -2, not a balance of 5.
        // The variance IS the product: it is how shrinkage gets measured.
        var stocktake = Start();
        var variantId = Guid.CreateVersion7();

        stocktake.RecordCount(variantId, countedQuantity: 5m, expectedQuantity: 7m, Guid.CreateVersion7(), Now);

        stocktake.Lines[0].Variance.ShouldBe(-2m);
    }

    [Fact]
    public void Posting_yields_adjustment_movements_only_for_lines_with_variance()
    {
        var stocktake = Start();
        var withVariance = Guid.CreateVersion7();
        var matching = Guid.CreateVersion7();

        stocktake.RecordCount(withVariance, 5m, 7m, Guid.CreateVersion7(), Now);
        stocktake.RecordCount(matching, 3m, 3m, Guid.CreateVersion7(), Now);
        stocktake.CompleteCounting();

        var result = stocktake.Post(Guid.CreateVersion7(), Now);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].VariantId.ShouldBe(withVariance);
        result.Value[0].QuantityDelta.ShouldBe(-2m);
    }

    [Fact]
    public void A_stocktake_cannot_be_posted_without_review()
    {
        var stocktake = Start();
        stocktake.RecordCount(Guid.CreateVersion7(), 5m, 7m, Guid.CreateVersion7(), Now);

        stocktake.Post(Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_stocktake_cannot_be_completed()
    {
        Start().CompleteCounting().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_recount_replaces_the_previous_count_and_is_tracked()
    {
        var stocktake = Start();
        var variantId = Guid.CreateVersion7();

        stocktake.RecordCount(variantId, 5m, 7m, Guid.CreateVersion7(), Now);
        stocktake.RecordCount(variantId, 6m, 7m, Guid.CreateVersion7(), Now.AddMinutes(5));

        stocktake.Lines.Count.ShouldBe(1);
        stocktake.Lines[0].CountedQuantity.ShouldBe(6m);
        stocktake.Lines[0].RecountCount.ShouldBe(1);
    }

    [Fact]
    public void A_negative_count_is_rejected()
    {
        // You cannot physically count minus three of something. A negative BALANCE is
        // legitimate; a negative COUNT is a typo.
        Start().RecordCount(Guid.CreateVersion7(), -3m, 7m, Guid.CreateVersion7(), Now)
               .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Counts_cannot_be_recorded_after_counting_closes()
    {
        var stocktake = Start();
        stocktake.RecordCount(Guid.CreateVersion7(), 5m, 7m, Guid.CreateVersion7(), Now);
        stocktake.CompleteCounting();

        stocktake.RecordCount(Guid.CreateVersion7(), 1m, 1m, Guid.CreateVersion7(), Now)
                 .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void The_expected_quantity_at_time_of_count_is_preserved()
    {
        // Between counting and posting the store keeps trading, so the balance moves.
        // The variance the counter observed is the one that must remain explainable.
        var stocktake = Start();
        var variantId = Guid.CreateVersion7();

        stocktake.RecordCount(variantId, 5m, 7m, Guid.CreateVersion7(), Now);

        stocktake.Lines[0].ExpectedQuantity.ShouldBe(7m);
    }

    [Fact]
    public void A_stocktake_can_be_cancelled_while_counting()
    {
        var stocktake = Start();
        var canceller = Guid.CreateVersion7();

        var result = stocktake.Cancel("Wrong warehouse", canceller, Now);

        result.IsSuccess.ShouldBeTrue();
        stocktake.Status.ShouldBe(StocktakeStatus.Cancelled);
        stocktake.CancelledByUserId.ShouldBe(canceller);
        stocktake.CancellationReason.ShouldBe("Wrong warehouse");
    }

    [Fact]
    public void A_stocktake_can_be_cancelled_while_pending_review()
    {
        var stocktake = Start();
        stocktake.RecordCount(Guid.CreateVersion7(), 5m, 7m, Guid.CreateVersion7(), Now);
        stocktake.CompleteCounting();

        stocktake.Cancel("No longer needed", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        stocktake.Status.ShouldBe(StocktakeStatus.Cancelled);
    }

    [Fact]
    public void Cancelling_a_stocktake_requires_a_reason()
    {
        var stocktake = Start();

        var result = stocktake.Cancel("", Guid.CreateVersion7(), Now);

        result.IsFailure.ShouldBeTrue();
        stocktake.Status.ShouldBe(StocktakeStatus.Counting);
    }

    [Fact]
    public void A_posted_stocktake_cannot_be_cancelled()
    {
        // The adjustments are already real ledger movements at this point; unwinding
        // them is a correcting count, not a cancellation.
        var stocktake = Start();
        stocktake.RecordCount(Guid.CreateVersion7(), 5m, 7m, Guid.CreateVersion7(), Now);
        stocktake.CompleteCounting();
        stocktake.Post(Guid.CreateVersion7(), Now);

        var result = stocktake.Cancel("Too late", Guid.CreateVersion7(), Now);

        result.IsFailure.ShouldBeTrue();
        stocktake.Status.ShouldBe(StocktakeStatus.Posted);
    }
}
