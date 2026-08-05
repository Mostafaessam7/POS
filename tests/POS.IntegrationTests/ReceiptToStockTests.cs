using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Identity.Authorization;
using POS.Inventory.Domain;
using POS.Inventory.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Goods receipts actually move stock, at landed cost.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the whole purchasing-to-inventory seam exists to make pass, and the
/// numbers are not arbitrary: they are the ones HANDOVER records the walking skeleton
/// producing over real HTTP, which until now had never been reproduced through the real
/// API against a real database.
/// </para>
/// <para>
/// THE NUMBER THAT MATTERS IS 10.80. Two deliveries against one order, at different
/// landed costs, must blend into a weighted average of 10.80 — not 10.00. If it comes
/// out as 10.00 the freight never reached Inventory, which is the exact failure that
/// makes every margin report overstate itself and is invisible without this assertion.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class ReceiptToStockTests(ApiFixture fixture)
{
    [Fact]
    public async Task Two_receipts_at_different_landed_costs_blend_to_a_weighted_average()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await BuyerClientAsync(org);

        using var _client = client;

        var supplierId = await CreateSupplierAsync(client, org);
        var variantId = Guid.CreateVersion7();

        // 100 units at 10.00. Approval is not in the way: 1,000 is not ABOVE the
        // 1,000 floor, so the order goes straight to Approved.
        var order = await RaiseOrderAsync(client, org, supplierId, variantId, quantity: 100m, unitPrice: 10m);
        order.Status.ShouldBe("Approved");

        await SendAsync(client, order.Id);

        // First delivery: 60 units, 60.00 freight. 60.00 over 600.00 of goods is
        // 1.00 a unit, so landed cost is 11.00.
        await ReceiveAsync(client, order.Id, variantId, quantity: 60m, unitPrice: 10m, freight: 60m);

        var afterFirst = await BalanceAsync(org, variantId);
        afterFirst.ShouldNotBeNull();
        afterFirst.QuantityOnHand.ShouldBe(60m);
        afterFirst.AverageUnitCost.Amount.ShouldBe(11.00m);

        // Second delivery: 40 units, 20.00 freight. 0.50 a unit, so landed 10.50.
        await ReceiveAsync(client, order.Id, variantId, quantity: 40m, unitPrice: 10m, freight: 20m);

        var afterSecond = await BalanceAsync(org, variantId);
        afterSecond.ShouldNotBeNull();
        afterSecond.QuantityOnHand.ShouldBe(100m);

        // (60 x 11.00 + 40 x 10.50) / 100 = 10.80
        afterSecond.AverageUnitCost.Amount.ShouldBe(10.80m);
        afterSecond.TotalValue.Amount.ShouldBe(1080.00m);
    }

    /// <summary>
    /// Posting the same receipt twice moves stock once.
    /// </summary>
    /// <remarks>
    /// The ledger is append-only, so a duplicate movement can never be corrected away —
    /// only offset, leaving two wrong-looking rows forever. This is also the path a
    /// crash between the stock write and the receipt save takes on retry, so it is a
    /// recovery test as much as a duplicate-submit test.
    /// </remarks>
    [Fact]
    public async Task Posting_a_receipt_twice_moves_stock_once()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await BuyerClientAsync(org);

        using var _client = client;

        var supplierId = await CreateSupplierAsync(client, org);
        var variantId = Guid.CreateVersion7();

        var order = await RaiseOrderAsync(client, org, supplierId, variantId, quantity: 50m, unitPrice: 10m);
        await SendAsync(client, order.Id);

        var receiptId = await CreateReceiptAsync(client, order.Id, variantId, quantity: 50m, unitPrice: 10m, freight: 0m);

        var first = await client.PostAsync(
            new Uri($"/api/v1/purchasing/receipts/{receiptId}/post", UriKind.Relative), content: null);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The aggregate refuses a second post outright, which is the first line of
        // defence. The idempotency guard in the stock adapter is the second, and covers
        // the case the aggregate cannot see: a retry after the stock moved but before
        // the receipt was saved.
        var second = await client.PostAsync(
            new Uri($"/api/v1/purchasing/receipts/{receiptId}/post", UriKind.Relative), content: null);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var movements = await fixture.ReadAsync<InventoryDbContext, int>(org.TenantId, db =>
            db.StockMovements.CountAsync(m => m.Reference.DocumentId == receiptId));

        movements.ShouldBe(1);

        var balance = await BalanceAsync(org, variantId);
        balance!.QuantityOnHand.ShouldBe(50m);
    }

    /// <summary>A dispatched supplier return takes the goods back off the shelf.</summary>
    [Fact]
    public async Task Dispatching_a_supplier_return_reduces_stock()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await BuyerClientAsync(org);

        using var _client = client;

        var supplierId = await CreateSupplierAsync(client, org);
        var variantId = Guid.CreateVersion7();

        var order = await RaiseOrderAsync(client, org, supplierId, variantId, quantity: 30m, unitPrice: 10m);
        await SendAsync(client, order.Id);
        await ReceiveAsync(client, order.Id, variantId, quantity: 30m, unitPrice: 10m, freight: 0m);

        (await BalanceAsync(org, variantId))!.QuantityOnHand.ShouldBe(30m);

        var returnResponse = await client.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            supplierId,
            branchId = org.BranchId,
            warehouseId = org.WarehouseId,
            returnNumber = $"RTN-{Guid.CreateVersion7():N}"[..20],
            currency = "USD",
            reason = 1, // Damaged
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            lines = new[] { new { variantId, quantity = 12m, unitCost = 10m } }
        });

        returnResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await returnResponse.Content.ReadFromJsonAsync<ReturnResponse>();

        var dispatched = await client.PostAsync(
            new Uri($"/api/v1/purchasing/returns/{created!.Id}/dispatch", UriKind.Relative), content: null);

        dispatched.StatusCode.ShouldBe(HttpStatusCode.OK);

        var balance = await BalanceAsync(org, variantId);
        balance!.QuantityOnHand.ShouldBe(18m);

        // The return consumes at the prevailing average and must not disturb it: taking
        // goods back out at the cost they came in at leaves the remaining stock valued
        // exactly as before.
        balance.AverageUnitCost.Amount.ShouldBe(10.00m);
    }

    private Task<StockBalance?> BalanceAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        Guid variantId) =>
        fixture.ReadAsync<InventoryDbContext, StockBalance?>(org.TenantId, db =>
            db.StockBalances
              .AsNoTracking()
              .FirstOrDefaultAsync(b => b.WarehouseId == org.WarehouseId && b.VariantId == variantId));

    private Task<(HttpClient Client, Guid UserId)> BuyerClientAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.OrderApproveDirector,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Purchasing.ReturnCreate,
            Permissions.Purchasing.ReturnDispatch);

    private static async Task<Guid> CreateSupplierAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org)
    {
        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = $"S{Random.Shared.Next(100000, 999999)}",
            name = "Landed Cost Supplier",
            currency = "USD"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }

    private static async Task<OrderResponse> RaiseOrderAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        Guid supplierId,
        Guid variantId,
        decimal quantity,
        decimal unitPrice)
    {
        var response = await client.PostAsJsonAsync("/api/v1/purchasing/orders", new
        {
            supplierId,
            companyId = org.CompanyId,
            branchId = org.BranchId,
            warehouseId = org.WarehouseId,
            orderNumber = $"PO-{Guid.CreateVersion7():N}"[..20],
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            expectedDeliveryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            lines = new[] { new { variantId, quantity, unitPrice } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static async Task SendAsync(HttpClient client, Guid orderId)
    {
        var response = await client.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{orderId}/send", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> CreateReceiptAsync(
        HttpClient client,
        Guid orderId,
        Guid variantId,
        decimal quantity,
        decimal unitPrice,
        decimal freight)
    {
        object payload = freight > 0m
            ? new
            {
                purchaseOrderId = orderId,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } },

                // Allocated by VALUE, which for a single-line receipt is the whole
                // charge spread over that line's units.
                landedCosts = new[] { new { type = 1, amount = freight, reference = $"FRT-{Guid.CreateVersion7():N}"[..12], basis = 1 } }
            }
            : new
            {
                purchaseOrderId = orderId,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } }
            };

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/receipts", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<ReceiptResponse>())!.Id;
    }

    private static async Task ReceiveAsync(
        HttpClient client,
        Guid orderId,
        Guid variantId,
        decimal quantity,
        decimal unitPrice,
        decimal freight)
    {
        var receiptId = await CreateReceiptAsync(client, orderId, variantId, quantity, unitPrice, freight);

        var posted = await client.PostAsync(
            new Uri($"/api/v1/purchasing/receipts/{receiptId}/post", UriKind.Relative), content: null);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record CreatedId(Guid Id);

    private sealed record OrderResponse(Guid Id, string OrderNumber, string Status, decimal TotalValue);

    private sealed record ReceiptResponse(Guid Id, string ReceiptNumber, string Status, decimal GoodsValue, decimal LandedCostTotal);

    private sealed record ReturnResponse(Guid Id, string ReturnNumber, string Status);
}
