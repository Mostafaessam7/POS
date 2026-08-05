using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The Inventory API: reading stock, and adjusting it by hand.
/// </summary>
/// <remarks>
/// Until now stock was only observable through a DbContext or the reconciliation report.
/// These tests drive the read endpoints against stock that a real posted receipt put
/// there, and exercise the one hand-driven write.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class InventoryApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task A_posted_receipt_is_visible_as_a_balance()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        var variantId = await ReceiveStockAsync(buyer, org, quantity: 40m, unitPrice: 10m, freight: 20m);

        var (viewer, _) = await ViewerClientAsync(org);
        using var _viewer = viewer;

        var balance = await viewer.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");

        balance.ShouldNotBeNull();
        balance.QuantityOnHand.ShouldBe(40m);

        // 400 goods + 20 freight over 40 units = 10.50 landed.
        balance.AverageUnitCost.ShouldBe(10.50m);
        balance.TotalValue.ShouldBe(420.00m);
        balance.IsNegative.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_variant_has_no_balance()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (viewer, _) = await ViewerClientAsync(org);
        using var _viewer = viewer;

        var response = await viewer.GetAsync(new Uri(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{Guid.CreateVersion7()}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_movement_ledger_shows_why_the_balance_is_what_it_is()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        var variantId = await ReceiveStockAsync(buyer, org, quantity: 15m, unitPrice: 4m, freight: 0m);

        var (viewer, _) = await ViewerClientAsync(org);
        using var _viewer = viewer;

        var movements = await viewer.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/variants/{variantId}/movements");

        movements.ShouldNotBeNull();
        movements.Count.ShouldBe(1);
        movements[0].Type.ShouldBe("Receipt");
        movements[0].QuantityDelta.ShouldBe(15m);
        movements[0].DocumentType.ShouldBe("PurchaseReceipt");
    }

    /// <summary>A wastage write-off reduces stock at the prevailing average and is reason-coded.</summary>
    [Fact]
    public async Task A_wastage_adjustment_reduces_stock_at_the_prevailing_average()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        var variantId = await ReceiveStockAsync(buyer, org, quantity: 40m, unitPrice: 10m, freight: 20m);

        var (adjuster, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.AdjustmentCreate, Permissions.Inventory.View);
        using var _adjuster = adjuster;

        var response = await adjuster.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            warehouseId = org.WarehouseId,
            variantId,
            kind = 3, // Wastage
            quantity = 6m,
            reasonCode = "DAMAGE",
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var balance = await response.Content.ReadFromJsonAsync<BalanceDto>();
        balance!.QuantityOnHand.ShouldBe(34m);

        // Wastage consumes at the prevailing average and leaves it unchanged: taking
        // stock out at what it cost does not restate the value of what remains.
        balance.AverageUnitCost.ShouldBe(10.50m);
        balance.TotalValue.ShouldBe(357.00m);
    }

    /// <summary>An increase adds units at a supplied cost and moves the weighted average.</summary>
    [Fact]
    public async Task An_increase_adjustment_blends_the_supplied_cost_into_the_average()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (buyer, _) = await BuyerClientAsync(org);
        using var _buyer = buyer;

        // 40 units landed at 10.50.
        var variantId = await ReceiveStockAsync(buyer, org, quantity: 40m, unitPrice: 10m, freight: 20m);

        var (adjuster, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.AdjustmentCreate);
        using var _adjuster = adjuster;

        // Found 10 more, valued at 12.00 each.
        var response = await adjuster.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            warehouseId = org.WarehouseId,
            variantId,
            kind = 1, // Increase
            quantity = 10m,
            unitCost = 12.00m,
            currency = "USD",
            reasonCode = "FOUND",
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var balance = await response.Content.ReadFromJsonAsync<BalanceDto>();
        balance!.QuantityOnHand.ShouldBe(50m);

        // (40 x 10.50 + 10 x 12.00) / 50 = 10.80.
        balance.AverageUnitCost.ShouldBe(10.80m);
        balance.TotalValue.ShouldBe(540.00m);
    }

    [Fact]
    public async Task An_adjustment_without_a_reason_is_rejected()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (adjuster, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.AdjustmentCreate);
        using var _adjuster = adjuster;

        var response = await adjuster.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            warehouseId = org.WarehouseId,
            variantId = Guid.CreateVersion7(),
            kind = 3,
            quantity = 1m,
            reasonCode = "",
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        problem!.Errors.Keys.ShouldContain("ReasonCode");
    }

    /// <summary>A reduction of a variant with no balance is refused, not driven negative from nothing.</summary>
    [Fact]
    public async Task A_reduction_of_an_unknown_variant_is_refused()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (adjuster, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.AdjustmentCreate);
        using var _adjuster = adjuster;

        var response = await adjuster.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            warehouseId = org.WarehouseId,
            variantId = Guid.CreateVersion7(),
            kind = 2, // Decrease
            quantity = 5m,
            reasonCode = "CORRECTION",
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reading_stock_requires_the_view_permission()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Catalog.ProductView);
        using var _client = client;

        var response = await client.GetAsync(new Uri(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private Task<(HttpClient Client, Guid UserId)> BuyerClientAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost);

    private Task<(HttpClient Client, Guid UserId)> ViewerClientAsync(
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        fixture.CreateClientWithPermissionsAsync(org.TenantId, Guid.Empty, Permissions.Inventory.View);

    private static async Task<Guid> ReceiveStockAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        decimal quantity,
        decimal unitPrice,
        decimal freight)
    {
        var supplierResponse = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = $"S{Random.Shared.Next(100000, 999999)}",
            name = "Inventory Supplier",
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

        object payload = freight > 0m
            ? new
            {
                purchaseOrderId = order.Id,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } },
                landedCosts = new[] { new { type = 1, amount = freight, reference = $"FRT-{Guid.CreateVersion7():N}"[..12], basis = 1 } }
            }
            : new
            {
                purchaseOrderId = order.Id,
                receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
                supplierDeliveryNote = (string?)null,
                lines = new[] { new { purchaseOrderLineNumber = 1, variantId, quantityReceived = quantity, unitPrice } }
            };

        var receiptResponse = await client.PostAsJsonAsync("/api/v1/purchasing/receipts", payload);
        receiptResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var receipt = (await receiptResponse.Content.ReadFromJsonAsync<CreatedId>())!;

        (await client.PostAsync(new Uri($"/api/v1/purchasing/receipts/{receipt.Id}/post", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        return variantId;
    }

    private sealed record CreatedId(Guid Id);

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
        Guid DocumentId);

    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);
}
