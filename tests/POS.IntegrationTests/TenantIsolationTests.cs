using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// LAYER 3 OF TENANT ENFORCEMENT: proof.
/// </summary>
/// <remarks>
/// Query filters and the write interceptor are mechanisms. This is the evidence.
///
/// The cases are GENERATED FROM THE ROUTE TABLE rather than hand-written, because a
/// hand-written suite covers the endpoints that existed the day it was written. New
/// endpoints are added weekly for three years; only generation keeps this true.
///
/// Assertion is 404, NOT 403. A 403 confirms the resource exists, which is itself a
/// small information leak across a security boundary — it lets an attacker
/// enumerate valid identifiers.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class TenantIsolationTests(ApiFixture fixture)
{
    [Theory]
    [MemberData(nameof(AllResourceEndpoints))]
    public async Task Cannot_read_another_tenants_resource(string method, string template)
    {
        var (tenantA, tenantB) = await fixture.CreateTwoTenantsAsync();

        var resourceId = await fixture.SeedResourceAsync(tenantB, template);
        var url = template.Replace("{id}", resourceId.ToString());

        var client = fixture.CreateClientFor(tenantA);
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            $"{method} {template} leaked a resource across a tenant boundary.");
    }

    [Fact]
    public async Task List_endpoints_never_include_another_tenants_rows()
    {
        var (tenantA, tenantB) = await fixture.CreateTwoTenantsAsync();

        await fixture.SeedProductAsync(tenantA, "Tenant A Widget");
        await fixture.SeedProductAsync(tenantB, "Tenant B Widget");

        var products = await fixture.CreateClientFor(tenantA)
            .GetFromJsonAsync<ProductListResponse>("/api/v1/catalog/products");

        products!.Items.ShouldAllBe(p => p.Name == "Tenant A Widget");
    }

    [Fact]
    public async Task Write_to_another_tenants_resource_is_blocked_by_the_interceptor()
    {
        var (tenantA, tenantB) = await fixture.CreateTwoTenantsAsync();
        var productId = await fixture.SeedProductAsync(tenantB, "Tenant B Widget");

        // Bypasses the API entirely and goes straight to the DbContext, simulating a
        // background job with a mis-scoped tenant context. The query filter does not
        // protect this path; the write interceptor must.
        await Should.ThrowAsync<InvalidOperationException>(
            () => fixture.AttemptDirectCrossTenantWriteAsync(tenantA, productId));
    }

    [Fact]
    public async Task Route_tenant_id_that_disagrees_with_the_token_is_rejected()
    {
        var (tenantA, tenantB) = await fixture.CreateTwoTenantsAsync();

        var response = await fixture.CreateClientFor(tenantA)
            .GetAsync($"/api/v1/tenants/{tenantB}/settings");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Every route exposing a tenant-scoped resource, discovered at runtime.</summary>
    public static TheoryData<string, string> AllResourceEndpoints() =>
        ApiFixture.DiscoverTenantScopedEndpoints();
}

public sealed record ProductListResponse(IReadOnlyList<ProductSummary> Items);
public sealed record ProductSummary(Guid Id, string Name);
