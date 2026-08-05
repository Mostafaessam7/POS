using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Per-tenant overrides of deployment-wide policy defaults — ADR 049's originally
/// deferred per-tenant configuration, now backed by a real settings store instead of
/// the single deployment-wide <c>appsettings.json</c> section every tenant used to
/// share.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class TenantSettingsApiTests(ApiFixture fixture)
{
    /// <summary>Before any override, a tenant sees the deployment-wide default.</summary>
    [Fact]
    public async Task An_unconfigured_tenant_reads_the_deployment_wide_default()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.SettingsEdit);
        using var _client = client;

        var policy = await client.GetFromJsonAsync<PurchasingPolicyDto>("/api/v1/settings/purchasing-policy");

        policy!.ApprovalRequiredAbove.ShouldBe(1_000m);
    }

    /// <summary>The whole point: raising a tenant's own threshold changes what its orders actually do.</summary>
    [Fact]
    public async Task Raising_a_tenants_approval_threshold_lets_a_previously_approval_requiring_order_through_unapproved()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (admin, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Administration.SettingsEdit,
            Permissions.Purchasing.OrderRaise);
        using var _admin = admin;

        // 2,000 needs approval under the 1,000 deployment default — the same fixture
        // PurchasingApiTests.Order_is_raised_approved_sent_and_received pins down.
        var beforeOverride = await RaiseOrderAsync(admin, org, supplierId, quantity: 100m, unitPrice: 20m);
        beforeOverride.Status.ShouldBe("PendingApproval");

        var putResponse = await admin.PutAsJsonAsync("/api/v1/settings/purchasing-policy", new
        {
            approvalRequiredAbove = 100_000m,
            allowSelfApproval = false,
            thresholds = new[]
            {
                new { fromValue = 100_000m, level = "Supervisor" },
                new { fromValue = 200_000m, level = "Manager" },
                new { fromValue = 500_000m, level = "Director" }
            },
            receiptTolerancePercentage = 5m,
            receiptToleranceUnits = 2m
        });
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterOverride = await RaiseOrderAsync(admin, org, supplierId, quantity: 100m, unitPrice: 20m);
        afterOverride.Status.ShouldBe("Approved");
    }

    /// <summary>One tenant's override must never leak into another tenant's behaviour.</summary>
    [Fact]
    public async Task A_tenants_override_does_not_affect_a_different_tenant()
    {
        var (overriddenOrg, plainOrg) = (await fixture.ProvisionOrganisationAsync(), await fixture.ProvisionOrganisationAsync());

        var (overriddenAdmin, _) = await fixture.CreateClientWithPermissionsAsync(
            overriddenOrg.TenantId, Guid.Empty,
            Permissions.Administration.SettingsEdit,
            Permissions.Purchasing.OrderRaise);
        using var _overriddenAdmin = overriddenAdmin;

        await overriddenAdmin.PutAsJsonAsync("/api/v1/settings/purchasing-policy", new
        {
            approvalRequiredAbove = 100_000m,
            allowSelfApproval = false,
            thresholds = new[] { new { fromValue = 100_000m, level = "Supervisor" } },
            receiptTolerancePercentage = 5m,
            receiptToleranceUnits = 2m
        });

        var (plainClient, _) = await fixture.CreateClientWithPermissionsAsync(
            plainOrg.TenantId, Guid.Empty, Permissions.Purchasing.OrderRaise);
        using var _plainClient = plainClient;

        var supplierId = await CreateSupplierAsync(plainOrg);
        var order = await RaiseOrderAsync(plainClient, plainOrg, supplierId, quantity: 100m, unitPrice: 20m);

        // The OTHER tenant's override raised the floor to 100,000 — this tenant never
        // set one, so it must still see the 1,000 deployment default and require
        // approval on a 2,000 order.
        order.Status.ShouldBe("PendingApproval");
    }

    /// <summary>Mirrors the Purchasing test above for the Inventory side of the same store.</summary>
    [Fact]
    public async Task An_unconfigured_tenant_reads_the_inventory_deployment_wide_default()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.SettingsEdit);
        using var _client = client;

        var policy = await client.GetFromJsonAsync<InventoryPolicyDto>("/api/v1/settings/inventory-policy");

        policy!.AllowSelfApproval.ShouldBeFalse();
    }

    [Fact]
    public async Task Setting_an_inventory_override_is_reflected_on_the_next_read()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.SettingsEdit);
        using var _client = client;

        var putResponse = await client.PutAsJsonAsync("/api/v1/settings/inventory-policy", new
        {
            allowSelfApproval = true,
            varianceWriteOffThresholds = new[] { new { fromValue = 0m, level = "Supervisor" } }
        });
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var policy = await client.GetFromJsonAsync<InventoryPolicyDto>("/api/v1/settings/inventory-policy");
        policy!.AllowSelfApproval.ShouldBeTrue();
    }

    [Fact]
    public async Task Reading_or_writing_settings_without_permission_is_forbidden()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.OrderRaise);
        using var _client = client;

        var getResponse = await client.GetAsync(new Uri("/api/v1/settings/purchasing-policy", UriKind.Relative));
        getResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<Guid> CreateSupplierAsync((Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org)
    {
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.SupplierManage);
        using var _client = client;

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = $"S{Random.Shared.Next(100000, 999999)}",
            name = "Settings Test Supplier",
            currency = "USD"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }

    private static async Task<OrderResponse> RaiseOrderAsync(
        HttpClient client,
        (Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org,
        Guid supplierId,
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
            lines = new[] { new { variantId = Guid.CreateVersion7(), quantity, unitPrice } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private sealed record CreatedId(Guid Id);

    private sealed record OrderResponse(Guid Id, string OrderNumber, string Status, decimal TotalValue);

    private sealed record PurchasingPolicyDto(decimal ApprovalRequiredAbove, bool AllowSelfApproval);

    private sealed record InventoryPolicyDto(bool AllowSelfApproval);
}
