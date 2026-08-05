using System.Net;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The host's edge surface: health checks and API documentation. Neither belongs to
/// a specific module, so neither has a home in one of the module-named test files.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ApiSurfaceTests(ApiFixture fixture)
{
    [Fact]
    public async Task Liveness_reports_healthy_without_authentication()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_reports_every_module_reachable()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // The nine schemas HANDOVER.md names — a readiness probe that silently
        // dropped one would report healthy while that module was unreachable.
        foreach (var module in new[]
                 {
                     "identity", "catalog", "inventory", "sales", "payments",
                     "fiscal", "purchasing", "expenses", "sync"
                 })
        {
            body.ShouldContain(module);
        }
    }

    /// <summary>The generated schema is real, machine-readable, and reachable — not aspirational README text.</summary>
    [Fact]
    public async Task The_OpenApi_document_is_served_when_docs_are_enabled()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("openapi");
    }

    /// <summary>The interactive Scalar page README.md has always pointed at actually exists now.</summary>
    [Fact]
    public async Task The_Scalar_reference_page_is_served_when_docs_are_enabled()
    {
        using var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/scalar", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
