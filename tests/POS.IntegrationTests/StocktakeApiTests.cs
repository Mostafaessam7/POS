using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using POS.Inventory.Domain;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Stocktakes: counting against a real balance and posting the variance to the ledger.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class StocktakeApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Posting_a_short_count_reduces_stock_by_the_variance_only()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        startResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;
        stocktake.Status.ShouldBe("Counting");

        // The system says 40; the count finds 34 — a shortfall of 6.
        var countResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/counts",
            new { variantId, countedQuantity = 34m });

        countResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var counted = (await countResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;
        counted.Lines.Single().ExpectedQuantity.ShouldBe(40m);
        counted.Lines.Single().Variance.ShouldBe(-6m);

        (await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/complete-counting", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var postResponse = await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await postResponse.Content.ReadFromJsonAsync<StocktakeDto>())!.Status.ShouldBe("Posted");

        var balance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");
        balance!.QuantityOnHand.ShouldBe(34m);

        // The variance consumes at the prevailing average, same as a wastage adjustment:
        // the average itself does not move.
        balance.AverageUnitCost.ShouldBe(10.00m);

        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/variants/{variantId}/movements");
        movements!.ShouldContain(m => m.Type == "StocktakeAdjustment" && m.QuantityDelta == -6m);
    }

    [Fact]
    public async Task A_matching_count_produces_no_variance_and_no_movement()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 20m, unitPrice: 5m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Cycle,
            isBlind = true,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/counts",
            new { variantId, countedQuantity = 20m });

        await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/complete-counting", UriKind.Relative), null);

        var postResponse = await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/variants/{variantId}/movements");

        // Only the original receipt — a zero-variance line yields no adjustment movement.
        movements!.ShouldNotContain(m => m.Type == "StocktakeAdjustment");
    }

    [Fact]
    public async Task Posting_the_same_stocktake_twice_does_not_double_apply_the_variance()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/counts",
            new { variantId, countedQuantity = 34m });

        await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/complete-counting", UriKind.Relative), null);

        var first = await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var balance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");
        balance!.QuantityOnHand.ShouldBe(34m);
    }

    [Fact]
    public async Task A_recount_replaces_the_earlier_count_rather_than_adding_to_it()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync($"/api/v1/inventory/stocktakes/{stocktake.Id}/counts", new { variantId, countedQuantity = 30m });

        var recount = await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/counts", new { variantId, countedQuantity = 38m });

        recount.StatusCode.ShouldBe(HttpStatusCode.OK);
        var recounted = (await recount.Content.ReadFromJsonAsync<StocktakeDto>())!;
        recounted.Lines.Count.ShouldBe(1);
        recounted.Lines.Single().CountedQuantity.ShouldBe(38m);
        recounted.Lines.Single().Variance.ShouldBe(-2m);
    }

    [Fact]
    public async Task Posting_before_completing_counting_is_rejected()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 5m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync($"/api/v1/inventory/stocktakes/{stocktake.Id}/counts", new { variantId, countedQuantity = 8m });

        var response = await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_stocktake_can_be_cancelled_before_it_is_posted()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 5m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync($"/api/v1/inventory/stocktakes/{stocktake.Id}/counts", new { variantId, countedQuantity = 8m });

        var cancelResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/cancel",
            new { reason = "Wrong warehouse" });

        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancelResponse.Content.ReadFromJsonAsync<StocktakeDto>())!.Status.ShouldBe("Cancelled");
    }

    /// <summary>Once posted, the adjustments are real ledger movements — cancelling is no longer a pure status flip.</summary>
    [Fact]
    public async Task A_posted_stocktake_cannot_be_cancelled()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 5m);

        var startResponse = await client.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
        {
            warehouseId = org.WarehouseId,
            stocktakeNumber = $"ST-{Guid.CreateVersion7():N}"[..20],
            scope = (int)StocktakeScope.Full,
            isBlind = false,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var stocktake = (await startResponse.Content.ReadFromJsonAsync<StocktakeDto>())!;

        await client.PostAsJsonAsync($"/api/v1/inventory/stocktakes/{stocktake.Id}/counts", new { variantId, countedQuantity = 8m });
        await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/complete-counting", UriKind.Relative), null);
        await client.PostAsync(new Uri($"/api/v1/inventory/stocktakes/{stocktake.Id}/post", UriKind.Relative), null);

        var cancelResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/stocktakes/{stocktake.Id}/cancel",
            new { reason = "Too late" });

        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private Task<(HttpClient Client, Guid UserId)> OperatorClientAsync(Guid tenantId) =>
        fixture.CreateClientWithPermissionsAsync(
            tenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Inventory.View,
            Permissions.Inventory.CountPerform,
            Permissions.Inventory.CountApprove);

    private static async Task<Guid> ReceiveStockAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        decimal quantity,
        decimal unitPrice)
    {
        var supplierResponse = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = $"S{Random.Shared.Next(100000, 999999)}",
            name = "Stocktake Supplier",
            currency = "USD"
        });

        supplierResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var supplierId = (await supplierResponse.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var variantId = Guid.CreateVersion7();

        var orderResponse = await client.PostAsJsonAsync("/api/v1/purchasing/orders", new
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

        orderResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = (await orderResponse.Content.ReadFromJsonAsync<CreatedId>())!;

        (await client.PostAsync(new Uri($"/api/v1/purchasing/orders/{order.Id}/send", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var receiptResponse = await client.PostAsJsonAsync("/api/v1/purchasing/receipts", new
        {
            purchaseOrderId = order.Id,
            receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            supplierDeliveryNote = (string?)null,
            lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } }
        });

        receiptResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var receipt = (await receiptResponse.Content.ReadFromJsonAsync<CreatedId>())!;

        (await client.PostAsync(new Uri($"/api/v1/purchasing/receipts/{receipt.Id}/post", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        return variantId;
    }

    private sealed record CreatedId(Guid Id);

    private sealed record StocktakeLineDto(Guid VariantId, decimal CountedQuantity, decimal ExpectedQuantity, decimal Variance);

    private sealed record StocktakeDto(
        Guid Id,
        Guid WarehouseId,
        string StocktakeNumber,
        string Scope,
        bool IsBlind,
        string Status,
        List<StocktakeLineDto> Lines);

    private sealed record BalanceDto(
        Guid WarehouseId,
        Guid VariantId,
        decimal QuantityOnHand,
        decimal AverageUnitCost,
        decimal TotalValue,
        string Currency,
        bool IsNegative);

    private sealed record MovementDto(
        Guid Id,
        string Type,
        decimal QuantityDelta,
        decimal UnitCost,
        string DocumentType,
        Guid DocumentId,
        string? ReasonCode,
        DateTimeOffset OccurredAt,
        DateOnly BusinessDate);
}
