using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The Purchasing API end to end: authorization, validation, and the workflow.
/// </summary>
/// <remarks>
/// These go over HTTP through the real pipeline — authentication, the permission policy
/// provider, the validation filter, the tenant middleware and the aggregates. The
/// alternative, calling handlers directly, would skip exactly the layers that are new
/// here and that fail in interesting ways.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class PurchasingApiTests(ApiFixture fixture)
{
    private const string AllScopes = "00000000-0000-0000-0000-000000000000";

    [Fact]
    public async Task Supplier_is_created_and_listed()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.SupplierManage,
            Permissions.Purchasing.SupplierView);

        using var _client = client;

        var created = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = "ACME",
            name = "Acme Supplies",
            currency = "USD",
            paymentTermDays = 45,
            leadTimeDays = 10
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var suppliers = await client.GetFromJsonAsync<List<SupplierListItem>>("/api/v1/purchasing/suppliers");

        suppliers.ShouldNotBeNull();
        suppliers.ShouldContain(s => s.Code == "ACME" && s.PaymentTermDays == 45);
    }

    /// <summary>The validation filter, not the aggregate, must answer for a malformed request.</summary>
    /// <remarks>
    /// A 400 naming the field is something a client can act on. Letting the request
    /// reach the domain and surface as a 422 from deep inside an aggregate tells the
    /// caller a rule was broken but not which input broke it.
    /// </remarks>
    [Fact]
    public async Task Invalid_supplier_request_is_rejected_with_field_errors()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.SupplierManage);

        using var _client = client;

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = "",
            name = "Missing code",
            currency = "US"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();

        problem.ShouldNotBeNull();
        problem.Errors.Keys.ShouldContain("Code");
        problem.Errors.Keys.ShouldContain("Currency");
    }

    [Fact]
    public async Task Endpoint_is_forbidden_without_the_permission()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        // Holds an unrelated purchasing permission, so this is a permission check
        // failing rather than an unauthenticated request.
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.SupplierView);

        using var _client = client;

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = "NOPE",
            name = "Not allowed",
            currency = "USD"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Holding a permission is not the same as holding it HERE.
    /// </summary>
    /// <remarks>
    /// The policy gate passes — the user genuinely holds the capability — and the scope
    /// check is the only thing that can refuse. Without it, a supervisor at one branch
    /// could raise orders against another's budget.
    /// </remarks>
    [Fact]
    public async Task Permission_held_at_another_scope_does_not_reach_this_one()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var elsewhere = Guid.CreateVersion7();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, elsewhere, Permissions.Purchasing.SupplierManage);

        using var _client = client;

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
        {
            companyId = org.CompanyId,
            code = "SCOPE",
            name = "Wrong scope",
            currency = "USD"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("purchasing.scope_denied");
    }

    /// <summary>
    /// The approval ladder: a supervisor cannot sign off an order that needs a director.
    /// </summary>
    /// <remarks>
    /// This is the control the whole ladder exists for. The defaults put the Director
    /// threshold at 50,000, so a 60,000 order offered a Supervisor's authority must be
    /// refused — and refused by the domain, from the level the endpoint resolved out of
    /// the caller's permissions.
    /// </remarks>
    [Fact]
    public async Task Order_above_the_threshold_is_refused_a_supervisor_approval()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (raiser, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.OrderRaise);

        using var _raiser = raiser;

        var order = await RaiseOrderAsync(raiser, org, supplierId, quantity: 600m, unitPrice: 100m);

        var (supervisor, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.OrderApproveSupervisor);

        using var _supervisor = supervisor;

        var response = await supervisor.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{order.Id}/approve", UriKind.Relative), content: null);

        // 403, not 422: the domain classifies an insufficient approval level as
        // Forbidden, which is the right reading — the order is fine, the approver is
        // not senior enough for it.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("purchasing.approval_level_insufficient");
    }

    /// <summary>Separation of duties: the raiser may not approve their own order.</summary>
    [Fact]
    public async Task Raiser_cannot_approve_their_own_order()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (raiser, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.OrderApproveDirector);

        using var _raiser = raiser;

        var order = await RaiseOrderAsync(raiser, org, supplierId, quantity: 100m, unitPrice: 100m);

        var response = await raiser.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{order.Id}/approve", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The happy path: raise, approve as someone else, send, receive.</summary>
    [Fact]
    public async Task Order_is_raised_approved_sent_and_received()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (raiser, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.ReceiptCreate,
            Permissions.Purchasing.ReceiptPost,
            Permissions.Purchasing.ReceiptView);

        using var _raiser = raiser;

        // 2,000 is above the 1,000 approval floor but below the 10,000 Manager
        // threshold, so it needs approval and a Manager is senior enough to give it.
        // Exactly 1,000 would NOT need approval — RequiresApproval is a strict
        // greater-than — which is the sort of boundary worth pinning down in a test.
        var order = await RaiseOrderAsync(raiser, org, supplierId, quantity: 100m, unitPrice: 20m);
        order.Status.ShouldBe("PendingApproval");
        order.TotalValue.ShouldBe(2000m);

        var (approver, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.OrderApproveManager);

        using var _approver = approver;

        var approved = await approver.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{order.Id}/approve", UriKind.Relative), content: null);

        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<OrderResponse>())!.Status.ShouldBe("Approved");

        var sent = await raiser.PostAsync(
            new Uri($"/api/v1/purchasing/orders/{order.Id}/send", UriKind.Relative), content: null);

        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sent.Content.ReadFromJsonAsync<OrderResponse>())!.Status.ShouldBe("Sent");

        // 60 of 100 units, with 60.00 freight. Landed cost is what makes this receipt
        // worth recording rather than a stock count.
        var receiptResponse = await raiser.PostAsJsonAsync("/api/v1/purchasing/receipts", new
        {
            purchaseOrderId = order.Id,
            receiptNumber = $"GRN-{Guid.CreateVersion7():N}"[..20],
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            supplierDeliveryNote = "DN-1",
            lines = new[]
            {
                new { purchaseOrderLineNumber = 1, variantId = order.Lines[0].VariantId, quantityReceived = 60m, unitPrice = 20m }
            },
            landedCosts = new[]
            {
                new { type = 1, amount = 60m, reference = "FRT-1", basis = 1 }
            }
        });

        receiptResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var receipt = await receiptResponse.Content.ReadFromJsonAsync<ReceiptResponse>();
        receipt!.GoodsValue.ShouldBe(1200m);
        receipt.LandedCostTotal.ShouldBe(60m);

        var posted = await raiser.PostAsync(
            new Uri($"/api/v1/purchasing/receipts/{receipt.Id}/post", UriKind.Relative), content: null);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Recording an invoice makes it visible on the list endpoint, not just by id.</summary>
    [Fact]
    public async Task Invoice_is_recorded_and_listed()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Purchasing.OrderRaise,
            Permissions.Purchasing.InvoiceRecord,
            Permissions.Purchasing.InvoiceView);

        using var _client = client;

        var order = await RaiseOrderAsync(client, org, supplierId, quantity: 10m, unitPrice: 5m);
        var invoiceNumber = $"INV-{Guid.CreateVersion7():N}"[..20];

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/invoices", new
        {
            companyId = org.CompanyId,
            supplierId,
            purchaseOrderId = order.Id,
            supplierInvoiceNumber = invoiceNumber,
            currency = "USD",
            invoiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            lines = new[]
            {
                new { purchaseOrderLineNumber = 1, variantId = order.Lines[0].VariantId, quantity = 10m, unitPrice = 5m }
            }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<InvoiceListItem>())!;
        created.SupplierInvoiceNumber.ShouldBe(invoiceNumber);

        var list = await client.GetFromJsonAsync<List<InvoiceListItem>>("/api/v1/purchasing/invoices");

        list.ShouldNotBeNull();
        list.ShouldContain(i => i.Id == created.Id && i.SupplierInvoiceNumber == invoiceNumber);
    }

    /// <summary>Creating a supplier return makes it visible on the list endpoint, not just by id.</summary>
    [Fact]
    public async Task Supplier_return_is_created_and_listed()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var supplierId = await CreateSupplierAsync(org);

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Purchasing.ReturnCreate, Permissions.Purchasing.ReturnView);

        using var _client = client;

        var returnNumber = $"RTN-{Guid.CreateVersion7():N}"[..20];

        var response = await client.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            supplierId,
            branchId = org.BranchId,
            warehouseId = org.WarehouseId,
            returnNumber,
            currency = "USD",
            reason = 1,
            businessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 5m, unitCost = 10m } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ReturnListItem>())!;
        created.ReturnNumber.ShouldBe(returnNumber);

        var list = await client.GetFromJsonAsync<List<ReturnListItem>>("/api/v1/purchasing/returns");

        list.ShouldNotBeNull();
        list.ShouldContain(r => r.Id == created.Id && r.ReturnNumber == returnNumber);
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
            name = "Test Supplier",
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
            lines = new[]
            {
                new { variantId = Guid.CreateVersion7(), quantity, unitPrice }
            }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private sealed record CreatedId(Guid Id);

    private sealed record SupplierListItem(Guid Id, string Code, string Name, string Currency, bool IsActive, int PaymentTermDays);

    private sealed record OrderResponse(Guid Id, string OrderNumber, string Status, decimal TotalValue, IReadOnlyList<OrderLine> Lines);

    private sealed record OrderLine(int LineNumber, Guid VariantId, decimal QuantityOrdered);

    private sealed record ReceiptResponse(Guid Id, string ReceiptNumber, string Status, decimal GoodsValue, decimal LandedCostTotal);

    private sealed record InvoiceListItem(Guid Id, string SupplierInvoiceNumber, Guid PurchaseOrderId, string Status, decimal NetTotal, string? BlockReason);

    private sealed record ReturnListItem(Guid Id, string ReturnNumber, Guid SupplierId, string Status, decimal ExpectedCredit, decimal? CreditedAmount, string? CreditNoteNumber);

    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);

    private sealed record ProblemResponse(string Code, string Title);
}
