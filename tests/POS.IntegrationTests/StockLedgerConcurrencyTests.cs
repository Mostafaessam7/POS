using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Puts a real number behind ADR 026's claim instead of leaving it argued from
/// design alone. PROJECT_STATUS.md's "no load/performance testing" item named this
/// specifically: the checkout hot path (a quantity-only movement — a sale, a
/// wastage write-off) is supposed to take a lock-free relative-update path
/// (`SET QuantityOnHand = QuantityOnHand + @delta`, no read, no version check, no
/// retry) precisely so a popular product's throughput does not collapse under
/// concurrent contention the way a read-modify-write or a pessimistic lock would.
/// </summary>
/// <remarks>
/// This is a CORRECTNESS-UNDER-CONCURRENCY test with a coarse timing sanity check
/// attached, not a calibrated benchmark — CI hardware varies far too much for a
/// tight latency assertion to mean anything. What it DOES prove, on real
/// infrastructure rather than by argument: firing many concurrent lock-free
/// movements at the SAME balance row never loses an update. A read-modify-write
/// bug (load balance, compute new quantity, save) would fail this non-deterministically
/// under load while passing every single-threaded test in the suite — which is
/// exactly the class of bug ADR 026's design is meant to rule out structurally.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class StockLedgerConcurrencyTests(ApiFixture fixture)
{
    /// <summary>Generous on purpose — see this type's remarks on why this is not a calibrated benchmark.</summary>
    private static readonly TimeSpan GenerousCeiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Concurrent_wastage_adjustments_on_the_same_variant_lose_no_updates()
    {
        const int concurrentWriters = 40;
        const decimal initialStock = 500m;

        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await OperatorClientAsync(org.TenantId);
        using var _client = client;

        // unitPrice kept low so the order's total (500 x 1 = 500) stays under the
        // default 1,000 approval floor and can be sent immediately — this test is
        // about ledger concurrency, not the approval workflow.
        var variantId = await ReceiveStockAsync(client, org, quantity: initialStock, unitPrice: 1m);

        var stopwatch = Stopwatch.StartNew();

        var responses = await Task.WhenAll(Enumerable.Range(0, concurrentWriters).Select(_ => client.PostAsJsonAsync(
            "/api/v1/inventory/adjustments",
            new
            {
                warehouseId = org.WarehouseId,
                variantId,
                kind = "Wastage",
                quantity = 1m,
                reasonCode = "CONCURRENCY_TEST",
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow)
            })));

        stopwatch.Stop();

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);

        var balance = await client.GetFromJsonAsync<BalanceDto>(
            $"/api/v1/inventory/warehouses/{org.WarehouseId}/balances/{variantId}");

        // The number that actually matters: every one of the 40 concurrent writers
        // landed. A lost update here would show up as a balance higher than expected
        // — some writer's relative decrement silently overwritten by another's.
        balance!.QuantityOnHand.ShouldBe(initialStock - concurrentWriters);

        // A coarse sanity check, not a benchmark (see this type's remarks): the
        // lock-free path should not degrade badly enough that 40 tiny concurrent
        // writes take anywhere near this long. This exists to catch a regression
        // that accidentally reintroduces a lock or a retry loop on this path, not to
        // certify a specific throughput number.
        stopwatch.Elapsed.ShouldBeLessThan(GenerousCeiling);
    }

    private Task<(HttpClient Client, Guid UserId)> OperatorClientAsync(Guid tenantId) =>
        fixture.CreateClientWithPermissionsAsync(
            tenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Inventory.View,
            Permissions.Inventory.AdjustmentCreate);

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
            name = "Concurrency Test Supplier",
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

    private sealed record BalanceDto(
        Guid WarehouseId,
        Guid VariantId,
        decimal QuantityOnHand,
        decimal AverageUnitCost,
        decimal TotalValue,
        string Currency,
        bool IsNegative);
}
