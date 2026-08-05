using POS.Payments.Domain;
using POS.Payments.Reconciliation;
using POS.SharedKernel;

namespace POS.UnitTests;

public sealed class SettlementReconciliationTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static Payment Captured(decimal amount, string reference)
    {
        var payment = Payment.Initiate(
            Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PaymentKind.Sale, new Money(amount, "GBP"), IdempotencyKey.New(), "SCRIPTED", At,
            BusinessDate.Open(new DateOnly(2026, 7, 22)));

        payment.MarkCaptured(new Money(amount, "GBP"), reference, At);
        return payment;
    }

    private static Payment Indeterminate(decimal amount)
    {
        var payment = Payment.Initiate(
            Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PaymentKind.Sale, new Money(amount, "GBP"), IdempotencyKey.New(), "SCRIPTED", At,
            BusinessDate.Open(new DateOnly(2026, 7, 22)));

        payment.MarkIndeterminate("timeout", At);
        return payment;
    }

    private static SettlementRecord Settled(decimal amount, string reference)
        => new(reference, new Money(amount, "GBP"), At, "VISA");

    [Fact]
    public void A_fully_matched_batch_is_clean()
    {
        var report = SettlementReconciler.Reconcile(
            [Settled(40m, "r1"), Settled(25m, "r2")],
            [Captured(40m, "r1"), Captured(25m, "r2")]);

        report.IsClean.ShouldBeTrue();
        report.Matched.Count.ShouldBe(2);
        report.ExceptionCount.ShouldBe(0);
    }

    [Fact]
    public void Money_settled_that_we_never_recorded_is_flagged_separately()
    {
        var report = SettlementReconciler.Reconcile(
            [Settled(40m, "r1"), Settled(99m, "ghost")],
            [Captured(40m, "r1")]);

        report.SettledButNotRecorded.Count.ShouldBe(1);
        report.SettledButNotRecorded[0].ProviderReference.ShouldBe("ghost");
        report.RecordedButNotSettled.ShouldBeEmpty();
        report.IsClean.ShouldBeFalse();
    }

    [Fact]
    public void A_capture_the_acquirer_has_not_settled_is_a_different_category()
    {
        var report = SettlementReconciler.Reconcile(
            [Settled(40m, "r1")],
            [Captured(40m, "r1"), Captured(15m, "r2")]);

        report.RecordedButNotSettled.Count.ShouldBe(1);
        report.SettledButNotRecorded.ShouldBeEmpty(
            "these are not interchangeable — one harms the customer, the other the merchant");
    }

    [Fact]
    public void An_amount_difference_is_reported_as_a_mismatch_not_as_two_orphans()
    {
        var report = SettlementReconciler.Reconcile([Settled(42m, "r1")], [Captured(40m, "r1")]);

        report.AmountMismatches.Count.ShouldBe(1);
        report.SettledButNotRecorded.ShouldBeEmpty();
        report.RecordedButNotSettled.ShouldBeEmpty();
        report.NetVariance("GBP").Amount.ShouldBe(2m);
    }

    /// <summary>
    /// The reason <c>IsClean</c> is not derived from the net variance.
    /// </summary>
    [Fact]
    public void Offsetting_errors_net_to_zero_and_are_still_not_clean()
    {
        var report = SettlementReconciler.Reconcile(
            [Settled(50m, "r1"), Settled(30m, "r2")],
            [Captured(40m, "r1"), Captured(40m, "r2")]);

        report.NetVariance("GBP").Amount.ShouldBe(0m);
        report.IsClean.ShouldBeFalse(
            "one customer overcharged and another undercharged is not a balanced day");
        report.AmountMismatches.Count.ShouldBe(2);
    }

    [Fact]
    public void Unresolved_payments_are_carried_into_the_report()
    {
        var report = SettlementReconciler.Reconcile([], [Indeterminate(40m)]);

        report.StillIndeterminate.Count.ShouldBe(1);
        report.IsClean.ShouldBeFalse();
    }

    [Fact]
    public void An_indeterminate_payment_is_not_counted_as_recorded_but_unsettled()
    {
        var report = SettlementReconciler.Reconcile([], [Indeterminate(40m)]);

        report.RecordedButNotSettled.ShouldBeEmpty(
            "we never claimed to have captured it, so the merchant is not owed it yet");
    }

    [Fact]
    public void Matching_is_case_insensitive_because_acquirer_files_are_inconsistent()
    {
        var report = SettlementReconciler.Reconcile([Settled(40m, "R1")], [Captured(40m, "r1")]);

        report.Matched.Count.ShouldBe(1);
    }

    [Fact]
    public void An_empty_batch_against_no_payments_is_clean()
    {
        var report = SettlementReconciler.Reconcile([], []);

        report.IsClean.ShouldBeTrue();
        report.NetVariance("GBP").Amount.ShouldBe(0m);
    }
}
