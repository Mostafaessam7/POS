using POS.Sales.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class SaleAggregateTests
{
    private const string Ccy = "USD";
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Bd = new(2026, 7, 21);

    private static Sale OpenSale() => Sale.Open(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Ccy, new ReceiptNumber("T01", 1), Bd, Now);

    private static SaleLine Line(decimal qty = 1m, decimal price = 10m) => SaleLine.Create(
        Guid.CreateVersion7(), "Widget", qty, "EA", new Money(price, Ccy),
        "STD", 0.15m, false, new Money(6m, Ccy), null, null);

    private static Sale SaleWithOneLine()
    {
        var sale = OpenSale();
        sale.AddLine(Line());
        sale.ApplyPricing(
            [new LinePricing(1, Money.Zero(Ccy), new Money(10m, Ccy),
                             new Money(1.50m, Ccy), new Money(11.50m, Ccy))],
            Money.Zero(Ccy));
        return sale;
    }

    [Fact]
    public void A_new_sale_is_open()
    {
        OpenSale().Status.ShouldBe(SaleStatus.Open);
    }

    [Fact]
    public void Lines_are_numbered_sequentially_from_one()
    {
        var sale = OpenSale();
        sale.AddLine(Line());
        sale.AddLine(Line());
        sale.AddLine(Line());

        sale.Lines.Select(l => l.LineNumber).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Removing_a_line_resequences_the_rest()
    {
        // A gap in receipt line numbers looks like a deleted line to an auditor.
        var sale = OpenSale();
        sale.AddLine(Line());
        sale.AddLine(Line());
        sale.AddLine(Line());

        sale.RemoveLine(2);

        sale.Lines.Select(l => l.LineNumber).ShouldBe([1, 2]);
    }

    [Fact]
    public void A_line_in_a_different_currency_is_rejected()
    {
        var sale = OpenSale();
        var foreign = SaleLine.Create(
            Guid.CreateVersion7(), "Widget", 1m, "EA", new Money(10m, "EUR"),
            "STD", 0.15m, false, new Money(6m, "EUR"), null, null);

        sale.AddLine(foreign).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_sale_cannot_be_completed()
    {
        OpenSale().Complete(Now).Error.Code.ShouldBe("sales.empty_sale");
    }

    [Fact]
    public void An_undertendered_sale_cannot_be_completed()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(5m, Ccy), Now), terminalIsOnline: true);

        sale.Complete(Now).Error.Code.ShouldBe("sales.under_tendered");
    }

    [Fact]
    public void Exact_cash_completes_the_sale_with_no_change()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(11.50m, Ccy), Now), terminalIsOnline: true);

        sale.Complete(Now).IsSuccess.ShouldBeTrue();
        sale.Status.ShouldBe(SaleStatus.Completed);
        sale.ChangeGiven.IsZero.ShouldBeTrue();
    }

    [Fact]
    public void Overtendered_cash_produces_change()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(20m, Ccy), Now), terminalIsOnline: true);

        sale.Complete(Now).IsSuccess.ShouldBeTrue();
        sale.ChangeGiven.Amount.ShouldBe(8.50m);
    }

    [Fact]
    public void Split_tender_across_card_and_cash_completes()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Card, new Money(10m, Ccy), Now), terminalIsOnline: true);
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(1.50m, Ccy), Now), terminalIsOnline: true);

        sale.Complete(Now).IsSuccess.ShouldBeTrue();
        sale.Tenders.Count.ShouldBe(2);
    }

    [Fact]
    public void A_card_cannot_be_overtendered()
    {
        // Taking more on a card and returning cash is a refund-fraud and laundering
        // pattern, and card scheme rules prohibit it.
        var sale = SaleWithOneLine();

        sale.AddTender(Tender.Create(TenderMethod.Card, new Money(50m, Ccy), Now), terminalIsOnline: true)
            .Error.Code.ShouldBe("sales.overtender_not_allowed");
    }

    [Fact]
    public void A_gift_card_cannot_be_overtendered_either()
    {
        var sale = SaleWithOneLine();

        sale.AddTender(Tender.Create(TenderMethod.GiftCard, new Money(50m, Ccy), Now), terminalIsOnline: true)
            .IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData(TenderMethod.GiftCard)]
    [InlineData(TenderMethod.LoyaltyPoints)]
    [InlineData(TenderMethod.StoreCredit)]
    [InlineData(TenderMethod.Voucher)]
    public void Balance_bearing_instruments_are_refused_offline(TenderMethod method)
    {
        // ADR 038. These balances live centrally, so an offline redemption cannot be
        // checked and the same gift card can be spent on two disconnected terminals.
        var sale = SaleWithOneLine();

        var result = sale.AddTender(
            Tender.Create(method, new Money(5m, Ccy), Now), terminalIsOnline: false);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("sales.tender_requires_connectivity");
    }

    [Fact]
    public void Cash_is_always_accepted_offline()
    {
        // The reason offline retail works at all. If this ever fails, the platform's
        // core proposition has been broken.
        var sale = SaleWithOneLine();

        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(5m, Ccy), Now), terminalIsOnline: false)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_completed_sale_cannot_be_modified()
    {
        // ADR 007: corrections are new documents, never edits.
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(11.50m, Ccy), Now), terminalIsOnline: true);
        sale.Complete(Now);

        sale.AddLine(Line()).Error.Code.ShouldBe("sales.sale_not_open");
        sale.RemoveLine(1).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Completing_a_sale_raises_a_domain_event_stamped_with_the_business_date()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(11.50m, Ccy), Now), terminalIsOnline: true);
        sale.Complete(Now);

        var completed = sale.DomainEvents.OfType<SaleCompleted>().ShouldHaveSingleItem();
        completed.BusinessDate.ShouldBe(Bd);
        completed.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void A_sale_with_payment_taken_cannot_be_suspended()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Cash, new Money(5m, Ccy), Now), terminalIsOnline: true);

        sale.Suspend(Now).Error.Code.ShouldBe("sales.cannot_suspend_with_tenders");
    }

    [Fact]
    public void Resuming_transfers_ownership_to_exactly_one_terminal()
    {
        // Ownership transfer rather than replication is what prevents two tills
        // holding divergent copies that would later need merging. ADR 037.
        var sale = SaleWithOneLine();
        var otherTerminal = Guid.CreateVersion7();

        sale.Suspend(Now);
        sale.Resume(otherTerminal, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();

        sale.Status.ShouldBe(SaleStatus.Open);
        sale.OwningTerminalId.ShouldBe(otherTerminal);
    }

    [Fact]
    public void Only_a_suspended_sale_can_be_resumed()
    {
        OpenSale().Resume(Guid.CreateVersion7(), Now)
                  .Error.Code.ShouldBe("sales.sale_not_suspended");
    }

    [Fact]
    public void Cancelling_an_open_basket_is_distinct_from_voiding_a_completed_sale()
    {
        // "Never happened" and "happened and was undone" are different answers to an
        // auditor, so they are different states.
        var sale = SaleWithOneLine();
        sale.Cancel(Now, Guid.CreateVersion7(), "customer left");

        sale.Status.ShouldBe(SaleStatus.Cancelled);
        sale.Status.ShouldNotBe(SaleStatus.Voided);
    }

    [Fact]
    public void Balance_due_reflects_partial_tender()
    {
        var sale = SaleWithOneLine();
        sale.AddTender(Tender.Create(TenderMethod.Card, new Money(4m, Ccy), Now), terminalIsOnline: true);

        sale.BalanceDue.Amount.ShouldBe(7.50m);
    }

    [Fact]
    public void Margin_uses_the_cost_snapshotted_at_sale_time()
    {
        // Not today's average cost, which changes with every delivery and would
        // silently restate historical profitability.
        var sale = OpenSale();
        sale.AddLine(Line(qty: 2m));
        sale.ApplyPricing(
            [new LinePricing(1, Money.Zero(Ccy), new Money(20m, Ccy),
                             new Money(3m, Ccy), new Money(23m, Ccy))],
            Money.Zero(Ccy));

        sale.Lines.Single().Margin.Amount.ShouldBe(8m); // 20 net − (6 cost × 2)
    }

    [Theory]
    [InlineData(TenderMethod.Cash, false)]
    [InlineData(TenderMethod.GiftCard, true)]
    [InlineData(TenderMethod.LoyaltyPoints, true)]
    [InlineData(TenderMethod.StoreCredit, true)]
    public void Balance_bearing_instruments_require_connectivity(TenderMethod method, bool expected)
    {
        // Offline redemption of a centrally-held balance invites double-spend across
        // terminals. Cash always works, which is why offline retail works at all.
        method.RequiresConnectivity().ShouldBe(expected);
    }
}

public sealed class ShiftTests
{
    private const string Ccy = "USD";
    private static readonly DateTimeOffset Open = new(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Bd = new(2026, 7, 20);

    private static Shift OpenShift() => Shift.Open(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), new Money(200m, Ccy), Bd, Open);

    [Fact]
    public void The_business_date_is_fixed_at_shift_open_not_derived_per_sale()
    {
        // A bar opening at 20:00 and trading to 02:00 books everything to the same
        // trading day. Deriving it per sale from the wall clock splits the night
        // across two reports. ADR 017.
        OpenShift().BusinessDate.ShouldBe(Bd);
    }

    [Fact]
    public void Expected_cash_is_float_plus_sales_less_refunds_and_drops()
    {
        var shift = OpenShift();
        shift.RecordCashDrop(new Money(300m, Ccy), Guid.CreateVersion7(), Open.AddHours(3), "safe");

        var expected = shift.CalculateExpectedCash(
            cashSales: new Money(500m, Ccy), cashRefunds: new Money(50m, Ccy));

        expected.Amount.ShouldBe(350m); // 200 + 500 − 50 − 300
    }

    [Fact]
    public void A_pickup_increases_expected_cash()
    {
        var shift = OpenShift();
        shift.RecordCashPickup(new Money(100m, Ccy), Guid.CreateVersion7(), Open.AddHours(1), null);

        shift.CalculateExpectedCash(Money.Zero(Ccy), Money.Zero(Ccy)).Amount.ShouldBe(300m);
    }

    [Fact]
    public void Closing_records_variance_rather_than_silently_correcting_it()
    {
        // Variance by operator over time is one of the most reliable indicators of
        // till fraud, so it is preserved, never adjusted away.
        var shift = OpenShift();

        shift.Close(
            countedCash: new Money(690m, Ccy),
            cashSales: new Money(500m, Ccy),
            cashRefunds: Money.Zero(Ccy),
            closedAt: Open.AddHours(8));

        shift.ExpectedCash.Amount.ShouldBe(700m);
        shift.Variance.Amount.ShouldBe(-10m);
        shift.HasVariance.ShouldBeTrue();
        shift.Status.ShouldBe(ShiftStatus.Closed);
    }

    [Fact]
    public void A_balanced_drawer_reports_no_variance()
    {
        var shift = OpenShift();
        shift.Close(new Money(700m, Ccy), new Money(500m, Ccy), Money.Zero(Ccy), Open.AddHours(8));

        shift.HasVariance.ShouldBeFalse();
    }

    [Fact]
    public void Closing_raises_an_event_carrying_expected_counted_and_variance()
    {
        var shift = OpenShift();
        shift.Close(new Money(690m, Ccy), new Money(500m, Ccy), Money.Zero(Ccy), Open.AddHours(8));

        var closed = shift.DomainEvents.OfType<ShiftClosed>().ShouldHaveSingleItem();
        closed.Variance.Amount.ShouldBe(-10m);
    }

    [Fact]
    public void A_closed_shift_rejects_further_cash_movements()
    {
        var shift = OpenShift();
        shift.Close(new Money(700m, Ccy), new Money(500m, Ccy), Money.Zero(Ccy), Open.AddHours(8));

        shift.RecordCashDrop(new Money(10m, Ccy), Guid.CreateVersion7(), Open.AddHours(9), null)
             .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_or_negative_cash_movement_is_rejected()
    {
        var shift = OpenShift();

        shift.RecordCashDrop(Money.Zero(Ccy), Guid.CreateVersion7(), Open, null)
             .Error.Code.ShouldBe("shifts.invalid_cash_amount");
    }
}
