using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Stock transfers: dispatching stock into transit and receiving it at the destination,
/// including the variance path.
/// </summary>
/// <remarks>
/// Each test provisions its own destination and in-transit warehouse alongside the one
/// tenant provisioning creates, because a transfer needs three distinct locations and
/// provisioning only ever creates one.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class TransferApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task A_full_receipt_completes_the_transfer_and_moves_stock_both_legs()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 15m } }
        });

        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        transfer.Status.ShouldBe("Draft");

        var dispatchResponse = await client.PostAsync(
            new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null);
        dispatchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await dispatchResponse.Content.ReadFromJsonAsync<TransferDto>())!.Status.ShouldBe("InTransit");

        // Source gave up 15, in-transit doesn't stay observable through the balance
        // endpoint in this test — only source and eventual destination are asserted.
        var sourceAfterDispatch = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");
        sourceAfterDispatch!.QuantityOnHand.ShouldBe(25m);

        var receiveResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 15m } } });

        receiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = (await receiveResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        received.Status.ShouldBe("Completed");
        received.HasVariance.ShouldBeFalse();

        var destinationBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{destinationWarehouseId}/balances/{variantId}");
        destinationBalance!.QuantityOnHand.ShouldBe(15m);
        destinationBalance.AverageUnitCost.ShouldBe(10.00m);
    }

    [Fact]
    public async Task A_short_receipt_leaves_a_variance_that_can_be_written_off()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 10m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        (await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Only 7 of the 10 sent arrive.
        var receiveResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 7m } } });

        receiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = (await receiveResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        received.Status.ShouldBe("ReceivedWithVariance");
        received.HasVariance.ShouldBeTrue();

        var destinationBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{destinationWarehouseId}/balances/{variantId}");
        destinationBalance!.QuantityOnHand.ShouldBe(7m);

        // The missing 3 still exists somewhere, unaccounted for, until written off.
        var transitBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{transitWarehouseId}/balances/{variantId}");
        transitBalance!.QuantityOnHand.ShouldBe(3m);

        // Written off by someone OTHER than the receiver — see
        // Self_approval_of_a_variance_by_the_receiver_is_forbidden for why that matters.
        var (approver, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.TransferWriteOffVarianceSupervisor);
        using var _approver = approver;

        var writeOffResponse = await approver.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/write-off-variance",
            new { reasonCode = "SHRINKAGE" });

        writeOffResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var writtenOff = (await writeOffResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        writtenOff.Status.ShouldBe("Completed");

        var transitAfterWriteOff = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{transitWarehouseId}/balances/{variantId}");
        transitAfterWriteOff!.QuantityOnHand.ShouldBe(0m);
    }

    /// <summary>
    /// The rarer, opposite direction: more arrives than was ever dispatched. A
    /// miscount, not the theft case the control targets, but §6 item 9 notes the
    /// aggregate and service both handle it deliberately rather than by accident.
    /// </summary>
    [Fact]
    public async Task A_surplus_receipt_is_written_off_as_a_found_stock_increase_not_a_wastage()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 10m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        (await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 12 arrive against 10 sent — a surplus of 2, not a shortfall.
        var receiveResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 12m } } });

        receiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = (await receiveResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        received.Status.ShouldBe("ReceivedWithVariance");
        received.HasVariance.ShouldBeTrue();

        var destinationBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{destinationWarehouseId}/balances/{variantId}");
        destinationBalance!.QuantityOnHand.ShouldBe(12m);

        // Moving 12 out of an in-transit leg that only ever received 10 is exactly
        // where the surplus shows up: the leg goes negative until the write-off
        // below reconciles it. Permitted by policy (ADR 027) — see NegativeStockPolicy.
        var transitBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{transitWarehouseId}/balances/{variantId}");
        transitBalance!.QuantityOnHand.ShouldBe(-2m);

        var (approver, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.TransferWriteOffVarianceSupervisor);
        using var _approver = approver;

        var writeOffResponse = await approver.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/write-off-variance",
            new { reasonCode = "FOUND_STOCK" });

        writeOffResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var writtenOff = (await writeOffResponse.Content.ReadFromJsonAsync<TransferDto>())!;
        writtenOff.Status.ShouldBe("Completed");

        // The surplus write-off brings the negative in-transit leg back to zero...
        var transitAfterWriteOff = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{transitWarehouseId}/balances/{variantId}");
        transitAfterWriteOff!.QuantityOnHand.ShouldBe(0m);

        // ...and it does so as a found-stock INCREASE, never a Wastage movement —
        // Wastage is what a SHORTFALL write-off records, and recording a surplus the
        // same way would understate stock rather than correct it.
        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/warehouses/{transitWarehouseId}/variants/{variantId}/movements");
        movements.ShouldNotBeNull();
        movements.ShouldContain(m => m.Type == "AdjustmentIncrease" && m.QuantityDelta == 2m);
        movements.ShouldNotContain(m => m.Type == "Wastage");
    }

    /// <summary>The person who found the shortfall must not also be the one who disposes of it.</summary>
    [Fact]
    public async Task Self_approval_of_a_variance_by_the_receiver_is_forbidden()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        // Holds BOTH the operator permissions and the write-off permission, so the
        // rejection below comes from the aggregate's self-approval rule, not from
        // simply lacking the permission to attempt a write-off at all.
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Inventory.View,
            Permissions.Inventory.TransferCreate,
            Permissions.Inventory.TransferReceive,
            Permissions.Inventory.TransferWriteOffVarianceSupervisor);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 10m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 10m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        (await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 8m } } });

        // Same client received it AND holds the write-off permission — still refused.
        var writeOffResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/write-off-variance",
            new { reasonCode = "SHRINKAGE" });

        writeOffResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A high-value variance needs a more senior approver than a low-value one.</summary>
    [Fact]
    public async Task A_high_value_variance_requires_a_more_senior_write_off_approver()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        // 100 units at 9.00 each keeps the PURCHASE order itself (900) under Purchasing's
        // own no-approval threshold (1,000), so this test isn't also exercising order
        // approval. A 60-unit shortfall is worth 540 — above the default Manager
        // threshold (500) and below Director (5,000).
        var variantId = await ReceiveStockAsync(client, org, quantity: 100m, unitPrice: 9m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 100m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null);

        await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 40m } } });

        var (supervisor, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.TransferWriteOffVarianceSupervisor);
        using var _supervisor = supervisor;

        var deniedResponse = await supervisor.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/write-off-variance",
            new { reasonCode = "SHRINKAGE" });

        deniedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (manager, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Inventory.TransferWriteOffVarianceManager);
        using var _manager = manager;

        var allowedResponse = await manager.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/write-off-variance",
            new { reasonCode = "SHRINKAGE" });

        allowedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dispatching_the_same_transfer_twice_does_not_double_move_stock()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 40m, unitPrice: 10m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 5m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        var first = await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sourceBalance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");
        sourceBalance!.QuantityOnHand.ShouldBe(35m);
    }

    [Fact]
    public async Task Receiving_a_transfer_that_was_never_dispatched_is_rejected()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 5m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 5m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/receive",
            new { receivedQuantities = new[] { new { variantId, quantity = 5m } } });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_draft_transfer_can_be_cancelled()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 5m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        var cancelResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/cancel",
            new { reason = "Created in error" });

        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancelResponse.Content.ReadFromJsonAsync<TransferDto>())!.Status.ShouldBe("Cancelled");
    }

    /// <summary>Once stock has actually left the source warehouse, there is nothing left to "un-happen".</summary>
    [Fact]
    public async Task A_dispatched_transfer_cannot_be_cancelled()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        var variantId = await ReceiveStockAsync(client, org, quantity: 10m, unitPrice: 5m);

        var destinationWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.StockRoom);
        var transitWarehouseId = await fixture.CreateWarehouseAsync(org.TenantId, org.BranchId, WarehouseKind.Transit);

        var createResponse = await client.PostAsJsonAsync("/api/v1/inventory/transfers", new
        {
            sourceWarehouseId = org.WarehouseId,
            destinationWarehouseId,
            inTransitWarehouseId = transitWarehouseId,
            transferNumber = $"TR-{Guid.CreateVersion7():N}"[..20],
            lines = new[] { new { variantId, quantity = 5m } }
        });

        var transfer = (await createResponse.Content.ReadFromJsonAsync<TransferDto>())!;

        (await client.PostAsync(new Uri($"/api/v1/inventory/transfers/{transfer.Id}/dispatch", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var cancelResponse = await client.PostAsJsonAsync(
            $"/api/v1/inventory/transfers/{transfer.Id}/cancel",
            new { reason = "Changed my mind" });

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
            Permissions.Inventory.TransferCreate,
            Permissions.Inventory.TransferReceive);

    /// <summary>Puts stock into the tenant's provisioned warehouse via a real posted receipt.</summary>
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
            name = "Transfer Supplier",
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

    private sealed record TransferDto(
        Guid Id,
        Guid SourceWarehouseId,
        Guid DestinationWarehouseId,
        Guid InTransitWarehouseId,
        string TransferNumber,
        string Status,
        bool HasVariance);

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
