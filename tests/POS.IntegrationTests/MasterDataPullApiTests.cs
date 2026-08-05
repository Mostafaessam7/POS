using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using POS.Sync.Contracts;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The downward direction of sync: a terminal pulling master data instead of
/// uploading transactions. See <c>MasterDataPullService</c> for why every pull is a
/// full snapshot rather than a true incremental delta — the gap this closes is that
/// there was previously NO WAY AT ALL for a terminal to receive catalog data.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class MasterDataPullApiTests(ApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Pulling_master_data_returns_an_active_products_variant_and_barcode()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        using var client = fixture.CreateClientFor(tenant, terminal);

        var created = await CreateProductWithBarcodeAsync(client, "5901234123457");

        var response = await client.PostAsJsonAsync("/api/v1/sync/master-data/pull", new
        {
            protocolVersion = SyncProtocol.CurrentVersion,
            terminalId = terminal,
            cursors = new Dictionary<string, long>()
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PullResponseDto>())!;

        body.IsFullSnapshot.ShouldBeTrue();

        var change = body.Changes.Single(c => c.EntityType == "Product" && c.EntityId == created.VariantId);
        change.Operation.ShouldBe("Upsert");

        var payload = JsonSerializer.Deserialize<ProductPayloadDto>(change.Payload!, JsonOptions)!;
        payload.Sku.ShouldBe(created.Sku);
        payload.Barcodes.ShouldContain("5901234123457");
    }

    /// <summary>One tenant's catalogue must never leak into another tenant's pull response.</summary>
    [Fact]
    public async Task Pulling_master_data_never_returns_another_tenants_products()
    {
        var (tenantA, terminalA) = await fixture.CreateEnrolledTerminalAsync();
        var (tenantB, terminalB) = await fixture.CreateEnrolledTerminalAsync();

        using var clientA = fixture.CreateClientFor(tenantA, terminalA);
        using var clientB = fixture.CreateClientFor(tenantB, terminalB);

        var productA = await CreateProductWithBarcodeAsync(clientA, "5901234123457");

        var responseB = await clientB.PostAsJsonAsync("/api/v1/sync/master-data/pull", new
        {
            protocolVersion = SyncProtocol.CurrentVersion,
            terminalId = terminalB,
            cursors = new Dictionary<string, long>()
        });

        var bodyB = (await responseB.Content.ReadFromJsonAsync<PullResponseDto>())!;

        bodyB.Changes.ShouldNotContain(c => c.EntityId == productA.VariantId);
    }

    [Fact]
    public async Task Pulling_with_an_unsupported_protocol_version_is_rejected()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        using var client = fixture.CreateClientFor(tenant, terminal);

        var response = await client.PostAsJsonAsync("/api/v1/sync/master-data/pull", new
        {
            protocolVersion = "0.1",
            terminalId = terminal,
            cursors = new Dictionary<string, long>()
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<(Guid ProductId, Guid VariantId, string Sku)> CreateProductWithBarcodeAsync(
        HttpClient client, string barcode)
    {
        var sku = $"SKU-{Guid.CreateVersion7():N}";

        var createResponse = await client.PostAsJsonAsync("/api/v1/catalog/products", new { name = "Pull Test Product", sku });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await createResponse.Content.ReadFromJsonAsync<CreatedProductDto>())!;

        var barcodeResponse = await client.PostAsJsonAsync(
            $"/api/v1/catalog/products/{created.Id}/barcodes",
            new { value = barcode, symbology = 0 });
        barcodeResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (created.Id, created.VariantId, sku);
    }

    private sealed record CreatedProductDto(Guid Id, Guid VariantId);

    private sealed record PullResponseDto(
        IReadOnlyDictionary<string, long> Versions,
        IReadOnlyList<ChangeDto> Changes,
        bool IsFullSnapshot,
        bool HasMore);

    private sealed record ChangeDto(string EntityType, Guid EntityId, long Version, string Operation, string? Payload);

    private sealed record ProductPayloadDto(
        Guid ProductId,
        Guid VariantId,
        string Sku,
        string Name,
        decimal Price,
        string Currency,
        Guid TaxGroupId,
        IReadOnlyList<string> Barcodes);
}
