using System.Net;
using System.Net.Http.Json;
using POS.Api.Endpoints;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Editing and deactivating a product — the gap HANDOVER.md's §9 punch list named
/// ("the UI only exposes create"), which turned out to run deeper than the UI: no
/// update endpoint existed at all, only create/list/delete. This closes it with
/// <c>Product.Rename</c> and <c>PUT /api/v1/catalog/products/{id}</c>.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class CatalogApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Renaming_a_product_persists_the_new_name()
    {
        var tenantId = await fixture.CreateTenantAsync();
        var productId = await fixture.SeedProductAsync(tenantId, "Original Name");

        using var client = fixture.CreateClientFor(tenantId);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/catalog/products/{productId}", new UpdateProductRequest("Renamed Product"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<ProductSummaryResponse>())!;
        updated.Name.ShouldBe("Renamed Product");

        var list = (await (await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative)))
            .Content.ReadFromJsonAsync<ProductListResponse>())!;
        list.Items.ShouldContain(p => p.Id == productId && p.Name == "Renamed Product");
    }

    [Fact]
    public async Task Renaming_a_product_to_a_blank_name_is_rejected()
    {
        var tenantId = await fixture.CreateTenantAsync();
        var productId = await fixture.SeedProductAsync(tenantId, "Keeps Its Name");

        using var client = fixture.CreateClientFor(tenantId);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/catalog/products/{productId}", new UpdateProductRequest("   "));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var list = (await (await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative)))
            .Content.ReadFromJsonAsync<ProductListResponse>())!;
        list.Items.ShouldContain(p => p.Id == productId && p.Name == "Keeps Its Name");
    }

    [Fact]
    public async Task Renaming_an_unknown_product_is_not_found()
    {
        var tenantId = await fixture.CreateTenantAsync();
        using var client = fixture.CreateClientFor(tenantId);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/catalog/products/{Guid.CreateVersion7()}", new UpdateProductRequest("Doesn't Matter"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deactivating_a_product_removes_it_from_the_list()
    {
        var tenantId = await fixture.CreateTenantAsync();
        var productId = await fixture.SeedProductAsync(tenantId, "Going Away");

        await fixture.DeleteProductAsync(tenantId, productId);

        using var client = fixture.CreateClientFor(tenantId);
        var list = (await (await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative)))
            .Content.ReadFromJsonAsync<ProductListResponse>())!;

        list.Items.ShouldNotContain(p => p.Id == productId);
    }

    /// <summary>
    /// The primary variant id is exposed on the list, not just a product's own
    /// creation response — what closes the "GUIDs typed by hand" shortcut for any
    /// screen that needs to reference a variant (a purchase order line, a stock
    /// adjustment, a supplier return line).
    /// </summary>
    [Fact]
    public async Task Listing_products_exposes_the_primary_variant_id()
    {
        var tenantId = await fixture.CreateTenantAsync();
        var productId = await fixture.SeedProductAsync(tenantId, "Has A Variant");

        using var client = fixture.CreateClientFor(tenantId);

        var list = (await (await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative)))
            .Content.ReadFromJsonAsync<POS.Api.Endpoints.ProductListResponse>())!;

        var listed = list.Items.Single(p => p.Id == productId);
        listed.VariantId.ShouldNotBe(Guid.Empty);

        var single = (await (await client.GetAsync(
                new Uri($"/api/v1/catalog/products/{productId}", UriKind.Relative)))
            .Content.ReadFromJsonAsync<POS.Api.Endpoints.ProductSummaryResponse>())!;

        single.VariantId.ShouldBe(listed.VariantId);
    }
}
