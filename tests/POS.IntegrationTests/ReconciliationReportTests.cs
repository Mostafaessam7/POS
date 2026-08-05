using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Identity.Authorization;
using POS.Inventory.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The reconciliation reports, against real posted documents and a real ledger.
/// </summary>
/// <remarks>
/// A reconciliation report that has only ever been run on consistent data is not a
/// control — it is a report that has never said no. Each case here therefore proves
/// both directions: clean when the system agrees with itself, and specific when it does
/// not.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class ReconciliationReportTests(ApiFixture fixture)
{
    [Fact]
    public async Task Receipt_stock_reconciliation_is_clean_after_a_normal_posting()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        await PostReceiptAsync(buyer, org, businessDate, quantity: 40m, unitPrice: 10m, freight: 20m);

        var (auditor, _) = await AuditorClientAsync(org);
        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/receipt-stock-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();
        report.RecordsExamined.ShouldBe(1);
        report.IsClean.ShouldBeTrue();
        report.Discrepancies.ShouldBeEmpty();
        report.NetImpact.ShouldBe(0m);
    }

    /// <summary>
    /// A receipt whose stock movement went missing is reported, with its value.
    /// </summary>
    /// <remarks>
    /// This is the failure the whole report exists to catch: the crash window between
    /// the stock write and the receipt save, where a retry never happened. Simulated by
    /// deleting the movement, because there is no way to provoke the real race
    /// deterministically — but the report cannot tell the difference, which is the
    /// point.
    ///
    /// The financial impact is the LANDED value, 420.00 on 40 units at 10.00 plus 20.00
    /// of freight. Reporting the goods value alone would understate what is missing from
    /// stock by exactly the freight, which is the error the landed-cost design exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public async Task A_receipt_whose_stock_movement_vanished_is_reported_with_its_landed_value()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        var receiptId = await PostReceiptAsync(buyer, org, businessDate, quantity: 40m, unitPrice: 10m, freight: 20m);

        // Deleted BELOW the change tracker, and it has to be: InventoryDbContext refuses
        // a tracked delete outright because the ledger is append-only (ADR 008). That
        // guard is correct and stays; this test needs the state it protects against,
        // which in production arrives as a lost write or a restore, not as a Remove()
        // call anyone made.
        var deleted = await fixture.ReadAsync<InventoryDbContext, int>(org.TenantId, db =>
            db.StockMovements
              .Where(m => m.Reference.DocumentId == receiptId)
              .ExecuteDeleteAsync());

        deleted.ShouldBe(1);

        var (auditor, _) = await AuditorClientAsync(org);
        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/receipt-stock-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeFalse();
        report.Discrepancies.Count.ShouldBe(1);
        report.Discrepancies[0].Kind.ShouldBe("ReceiptWithoutStockMovement");
        report.Discrepancies[0].FinancialImpact.ShouldBe(420.00m);
    }

    /// <summary>A dispatched return with no credit note is money the merchant is owed.</summary>
    [Fact]
    public async Task Supplier_credit_reconciliation_flags_an_uncredited_return()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        var supplierId = await CreateSupplierAsync(buyer, org);

        var created = await buyer.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            supplierId,
            branchId = org.BranchId,
            warehouseId = org.WarehouseId,
            returnNumber = $"RTN-{Guid.CreateVersion7():N}"[..20],
            currency = "USD",
            reason = 1,
            businessDate,
            lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 5m, unitCost = 8m } }
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var supplierReturn = await created.Content.ReadFromJsonAsync<CreatedId>();

        var dispatched = await buyer.PostAsync(
            new Uri($"/api/v1/purchasing/returns/{supplierReturn!.Id}/dispatch", UriKind.Relative), content: null);

        dispatched.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (auditor, _) = await AuditorClientAsync(org);
        using var _auditor = auditor;

        // Grace period of zero: the goods left today and no credit note has arrived, so
        // it is outstanding immediately rather than in thirty days.
        var report = await auditor.GetFromJsonAsync<ReconciliationReport>(
            $"/api/v1/reports/supplier-credit-reconciliation?businessDate={businessDate:yyyy-MM-dd}&currency=USD&gracePeriodDays=0");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeFalse();
        report.Discrepancies.ShouldContain(d => d.FinancialImpact == 40.00m);
    }

    /// <summary>Inventory checking itself: the balance must be reproducible from the ledger.</summary>
    [Fact]
    public async Task Stock_balance_reconciliation_is_clean_after_posting()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        await PostReceiptAsync(buyer, org, businessDate, quantity: 25m, unitPrice: 4m, freight: 0m);

        var (auditor, _) = await AuditorClientAsync(org);
        using var _auditor = auditor;

        var report = await auditor.GetFromJsonAsync<BalanceReconciliationReport>(
            $"/api/v1/reports/stock-balance-reconciliation?warehouseId={org.WarehouseId}");

        report.ShouldNotBeNull();
        report.IsClean.ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_require_the_reconciliation_permission()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Reports.SalesView);

        using var _client = client;

        var response = await client.GetAsync(new Uri(
            $"/api/v1/reports/receipt-stock-reconciliation?businessDate={DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private Task<(HttpClient Client, Guid UserId)> BuyerClientAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Purchasing.ReturnCreate,
            Permissions.Purchasing.ReturnDispatch);

    private Task<(HttpClient Client, Guid UserId)> AuditorClientAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Reports.ReconciliationView);

    private static async Task<Guid> CreateSupplierAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org)
    {
        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = $"S{Random.Shared.Next(100000, 999999)}",
            name = "Reconciliation Supplier",
            currency = "USD"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }

    private static async Task<Guid> PostReceiptAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        DateOnly businessDate,
        decimal quantity,
        decimal unitPrice,
        decimal freight)
    {
        var supplierId = await CreateSupplierAsync(client, org);
        var variantId = Guid.CreateVersion7();

        var orderResponse = await client.PostAsJsonAsync("/api/v1/purchasing/orders", new
        {
            supplierId,
            companyId = org.CompanyId,
            branchId = org.BranchId,
            warehouseId = org.WarehouseId,
            orderNumber = $"PO-{Guid.CreateVersion7():N}"[..20],
            businessDate,
            expectedDeliveryDate = businessDate.AddDays(7),
            lines = new[] { new { variantId, quantity, unitPrice } }
        });

        orderResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await orderResponse.Content.ReadFromJsonAsync<CreatedId>();

        var sent = await client.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{order!.Id}/send", UriKind.Relative), content: null);

        sent.StatusCode.ShouldBe(HttpStatusCode.OK);

        object payload = freight > 0m
            ? new
            {
                purchaseOrderId = order.Id,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate,
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } },
                landedCosts = new[] { new { type = 1, amount = freight, reference = $"FRT-{Guid.CreateVersion7():N}"[..12], basis = 1 } }
            }
            : new
            {
                purchaseOrderId = order.Id,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate,
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } }
            };

        var receiptResponse = await client.PostAsJsonAsync("/api/v1/purchasing/receipts", payload);
        receiptResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var receipt = await receiptResponse.Content.ReadFromJsonAsync<CreatedId>();

        var posted = await client.PostAsync(
            new Uri($"/api/v1/purchasing/receipts/{receipt!.Id}/post", UriKind.Relative), content: null);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);

        return receipt.Id;
    }

    private sealed record CreatedId(Guid Id);

    private sealed record ReconciliationReport(
        string ReportName,
        int RecordsExamined,
        bool IsClean,
        decimal NetImpact,
        IReadOnlyList<DiscrepancyDto> Discrepancies);

    private sealed record DiscrepancyDto(string Kind, string Reference, string Detail, decimal FinancialImpact);

    private sealed record BalanceReconciliationReport(Guid WarehouseId, bool IsClean);
}
