using POS.Reconciliation.Domain;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

internal static class ReconFixtures
{
    public const string Gbp = "GBP";
    public static readonly DateOnly Today = new(2026, 7, 22);

    public static Money M(decimal amount) => new(amount, Gbp);

    public static SaleProjection Sale(Guid id, decimal total, string number = "R-001", bool voided = false) =>
        new(id, number, Guid.CreateVersion7(), Today, M(total), voided);

    public static FiscalDocumentProjection Doc(Guid saleId, decimal total, bool submitted = true) =>
        new(Guid.CreateVersion7(), saleId, Today, M(total), submitted);

    public static PaymentProjection Payment(
        Guid saleId,
        decimal amount,
        PaymentReconciliationStatus status = PaymentReconciliationStatus.Captured,
        decimal refunded = 0m) =>
        new(Guid.CreateVersion7(), saleId, Today, M(amount), M(refunded), status);
}

public sealed class SaleFiscalReconciliationTests
{
    [Fact]
    public void A_day_where_every_sale_has_a_matching_document_is_clean()
    {
        var saleId = Guid.CreateVersion7();
        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Doc(saleId, 100m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
        result.RecordsExamined.ShouldBe(2);
    }

    [Fact]
    public void A_sale_with_no_fiscal_document_is_money_taken_and_nothing_declared()
    {
        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(Guid.CreateVersion7(), 100m, "R-042")],
            [],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.SaleWithoutFiscalDocument);
        found.Reference.ShouldBe("R-042");
        found.FinancialImpact.ShouldBe(ReconFixtures.M(100m));
    }

    [Fact]
    public void A_fiscal_document_with_no_sale_means_we_declared_something_that_never_happened()
    {
        var result = SaleFiscalReconciler.Reconcile(
            [],
            [ReconFixtures.Doc(Guid.CreateVersion7(), 60m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.ShouldHaveSingleItem().Kind.ShouldBe(DiscrepancyKind.FiscalDocumentWithoutSale);
    }

    [Fact]
    public void A_voided_sale_legitimately_has_no_fiscal_document()
    {
        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(Guid.CreateVersion7(), 100m, voided: true)],
            [],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void Totals_that_disagree_are_reported_with_the_direction_of_the_difference()
    {
        var saleId = Guid.CreateVersion7();
        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Doc(saleId, 90m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.FiscalTotalMismatch);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(-10m));
    }

    [Fact]
    public void An_issued_but_unsubmitted_document_is_reported_even_though_the_totals_agree()
    {
        var saleId = Guid.CreateVersion7();
        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Doc(saleId, 100m, submitted: false)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.ShouldHaveSingleItem().Kind.ShouldBe(DiscrepancyKind.FiscalDocumentNotSubmitted);
    }

    [Fact]
    public void Offsetting_errors_do_not_make_a_run_clean()
    {
        // A missing document for 50 and a phantom document for 50 net to nothing. They
        // are two separate problems that happen to be the same size, and a report that
        // called this clean would be worse than no report at all.
        var missing = Guid.CreateVersion7();
        var phantom = Guid.CreateVersion7();

        var result = SaleFiscalReconciler.Reconcile(
            [ReconFixtures.Sale(missing, 50m)],
            [ReconFixtures.Doc(phantom, 50m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.NetImpact(ReconFixtures.Gbp).ShouldBe(ReconFixtures.M(100m));
        result.IsClean.ShouldBeFalse();
        result.Discrepancies.Count.ShouldBe(2);
    }
}

public sealed class SalePaymentReconciliationTests
{
    [Fact]
    public void A_sale_paid_in_full_is_clean()
    {
        var saleId = Guid.CreateVersion7();
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Payment(saleId, 100m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void A_split_tender_summing_to_the_total_is_clean()
    {
        var saleId = Guid.CreateVersion7();
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Payment(saleId, 60m), ReconFixtures.Payment(saleId, 40m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void A_sale_with_no_payment_at_all_is_goods_that_left_unpaid()
    {
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(Guid.CreateVersion7(), 100m, "R-007")],
            [],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.ShouldHaveSingleItem().Kind.ShouldBe(DiscrepancyKind.SaleWithoutPayment);
    }

    [Fact]
    public void An_indeterminate_payment_does_not_count_as_covering_the_sale_and_is_reported_separately()
    {
        // Both facts matter. Treating it as paid is how a shop gives goods away; treating
        // it as failed is how a customer is charged twice (ADR 044).
        var saleId = Guid.CreateVersion7();
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Payment(saleId, 100m, PaymentReconciliationStatus.Indeterminate)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.Count.ShouldBe(2);
        result.Discrepancies.ShouldContain(d => d.Kind == DiscrepancyKind.PaymentTotalMismatch);
        result.Discrepancies.ShouldContain(d => d.Kind == DiscrepancyKind.PaymentUnresolved);
    }

    [Fact]
    public void A_failed_payment_alongside_a_successful_retry_is_clean()
    {
        var saleId = Guid.CreateVersion7();
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [
                ReconFixtures.Payment(saleId, 100m, PaymentReconciliationStatus.Failed),
                ReconFixtures.Payment(saleId, 100m)
            ],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void A_refund_reduces_what_a_payment_covers()
    {
        var saleId = Guid.CreateVersion7();
        var result = SalePaymentReconciler.Reconcile(
            [ReconFixtures.Sale(saleId, 100m)],
            [ReconFixtures.Payment(saleId, 100m, refunded: 30m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.PaymentTotalMismatch);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(-30m));
    }

    [Fact]
    public void A_payment_referencing_a_sale_that_does_not_exist_is_reported()
    {
        var result = SalePaymentReconciler.Reconcile(
            [],
            [ReconFixtures.Payment(Guid.CreateVersion7(), 25m)],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.ShouldHaveSingleItem().Kind.ShouldBe(DiscrepancyKind.PaymentWithoutSale);
    }
}

public sealed class ReceiptStockReconciliationTests
{
    private static readonly Guid Variant = Guid.CreateVersion7();

    [Fact]
    public void A_receipt_whose_instructions_reached_the_ledger_is_clean()
    {
        var receiptId = Guid.CreateVersion7();
        var result = ReceiptStockReconciler.Reconcile(
            [new ReceiptLineProjection(receiptId, "GRN-1", Variant, 60m, ReconFixtures.M(660m))],
            [new StockMovementProjection(receiptId, Variant, 60m, ReconFixtures.M(660m))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void A_posted_receipt_with_no_stock_movement_is_exactly_what_ADR_052_left_unenforced()
    {
        // Purchasing posted, the application layer never called Inventory. Nothing at
        // compile time catches this, which is why the report exists.
        var result = ReceiptStockReconciler.Reconcile(
            [new ReceiptLineProjection(Guid.CreateVersion7(), "GRN-9", Variant, 60m, ReconFixtures.M(660m))],
            [],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.ReceiptWithoutStockMovement);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(660m));
    }

    [Fact]
    public void A_movement_that_landed_the_supplier_price_instead_of_the_landed_cost_is_caught()
    {
        // The exact failure the Phase 7 gate test guards against, seen from production.
        var receiptId = Guid.CreateVersion7();
        var result = ReceiptStockReconciler.Reconcile(
            [new ReceiptLineProjection(receiptId, "GRN-1", Variant, 60m, ReconFixtures.M(660m))],
            [new StockMovementProjection(receiptId, Variant, 60m, ReconFixtures.M(600m))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.ReceiptMovementMismatch);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(-60m));
    }
}

public sealed class SupplierCreditReconciliationTests
{
    [Fact]
    public void A_fully_credited_return_is_clean()
    {
        var result = SupplierCreditReconciler.Reconcile(
            [new SupplierReturnProjection(Guid.CreateVersion7(), "SR-1", Guid.CreateVersion7(),
                ReconFixtures.Today.AddDays(-10), ReconFixtures.M(500m), ReconFixtures.M(500m))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void Goods_returned_with_no_credit_are_reported_from_the_first_day()
    {
        var result = SupplierCreditReconciler.Reconcile(
            [new SupplierReturnProjection(Guid.CreateVersion7(), "SR-2", Guid.CreateVersion7(),
                ReconFixtures.Today.AddDays(-3), ReconFixtures.M(500m), Money.Zero(ReconFixtures.Gbp))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.ReturnWithoutCredit);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(500m));
        found.Detail.ShouldContain("3 days");
    }

    [Fact]
    public void Past_the_grace_period_the_wording_escalates()
    {
        var result = SupplierCreditReconciler.Reconcile(
            [new SupplierReturnProjection(Guid.CreateVersion7(), "SR-3", Guid.CreateVersion7(),
                ReconFixtures.Today.AddDays(-45), ReconFixtures.M(500m), Money.Zero(ReconFixtures.Gbp))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.Discrepancies.ShouldHaveSingleItem().Detail.ShouldContain("past the 30-day terms");
    }

    [Fact]
    public void A_short_credit_is_the_report_that_recovers_real_money()
    {
        // Returned 500, credited 450. Nothing forces these to match, which is the entire
        // point of ADR 054 — and 50 is quietly written off by any system that assumes it.
        var result = SupplierCreditReconciler.Reconcile(
            [new SupplierReturnProjection(Guid.CreateVersion7(), "SR-4", Guid.CreateVersion7(),
                ReconFixtures.Today.AddDays(-5), ReconFixtures.M(500m), ReconFixtures.M(450m))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        var found = result.Discrepancies.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DiscrepancyKind.CreditShortfall);
        found.FinancialImpact.ShouldBe(ReconFixtures.M(50m));
    }

    [Fact]
    public void An_over_credit_is_not_reported_as_a_shortfall()
    {
        var result = SupplierCreditReconciler.Reconcile(
            [new SupplierReturnProjection(Guid.CreateVersion7(), "SR-5", Guid.CreateVersion7(),
                ReconFixtures.Today.AddDays(-5), ReconFixtures.M(500m), ReconFixtures.M(520m))],
            ReconFixtures.Today,
            ReconFixtures.Gbp);

        result.IsClean.ShouldBeTrue();
    }
}
