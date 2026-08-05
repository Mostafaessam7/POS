using System.Net;
using System.Net.Http.Json;
using POS.Api.Endpoints;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The platform-bootstrap endpoint: creating a tenant, gated by a named, individually
/// revocable <see cref="POS.Identity.Domain.ProvisioningOperator"/> identity rather than
/// a single shared secret or a permission — see <see cref="RequireOperatorApiKeyFilter"/>
/// for why — plus the root-gated endpoints that mint and revoke those identities.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ProvisioningApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Creating_a_tenant_with_the_correct_operator_key_succeeds()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        org.TenantId.ShouldNotBe(Guid.Empty);
        org.CompanyId.ShouldNotBe(Guid.Empty);
        org.BranchId.ShouldNotBe(Guid.Empty);
        org.WarehouseId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Creating_a_tenant_without_an_operator_key_is_refused()
    {
        var response = await fixture.ProvisionTenantWithKeyAsync(operatorApiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_a_tenant_with_the_wrong_operator_key_is_refused()
    {
        var response = await fixture.ProvisionTenantWithKeyAsync("not-the-real-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The whole point of a named identity: the created tenant records WHICH operator provisioned it.</summary>
    [Fact]
    public async Task A_created_tenant_records_which_operator_provisioned_it()
    {
        var (operatorId, apiKey) = await fixture.MintOperatorAsync();

        var response = await fixture.ProvisionTenantWithKeyAsync(apiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var tenant = (await response.Content.ReadFromJsonAsync<ProvisionedTenant>())!;
        tenant.ProvisionedByOperatorId.ShouldBe(operatorId);
    }

    /// <summary>Two operators are two identities: each mints its own key, and one's key never matches the other's hash.</summary>
    [Fact]
    public async Task Two_operators_mint_two_different_keys_and_each_only_authorises_its_own()
    {
        var (firstId, firstKey) = await fixture.MintOperatorAsync();
        var (secondId, secondKey) = await fixture.MintOperatorAsync();

        firstId.ShouldNotBe(secondId);
        firstKey.ShouldNotBe(secondKey);

        var firstResponse = await fixture.ProvisionTenantWithKeyAsync(firstKey);
        var secondResponse = await fixture.ProvisionTenantWithKeyAsync(secondKey);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await firstResponse.Content.ReadFromJsonAsync<ProvisionedTenant>())!.ProvisionedByOperatorId.ShouldBe(firstId);
        (await secondResponse.Content.ReadFromJsonAsync<ProvisionedTenant>())!.ProvisionedByOperatorId.ShouldBe(secondId);
    }

    /// <summary>Revoking one operator's key must not disturb any other operator's ability to provision.</summary>
    [Fact]
    public async Task Revoking_one_operators_key_leaves_other_operators_working()
    {
        var (revokedId, revokedKey) = await fixture.MintOperatorAsync();
        var (_, otherKey) = await fixture.MintOperatorAsync();

        (await fixture.RevokeOperatorAsync(revokedId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var revokedResponse = await fixture.ProvisionTenantWithKeyAsync(revokedKey);
        revokedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var otherResponse = await fixture.ProvisionTenantWithKeyAsync(otherKey);
        otherResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Revoking_an_operator_twice_is_idempotent()
    {
        var (operatorId, _) = await fixture.MintOperatorAsync();

        var first = await fixture.RevokeOperatorAsync(operatorId);
        var second = await fixture.RevokeOperatorAsync(operatorId);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_an_unknown_operator_is_not_found()
    {
        var response = await fixture.RevokeOperatorAsync(Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Minting_an_operator_without_the_root_key_is_refused()
    {
        var response = await fixture.MintOperatorViaHttpAsync(rootApiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Minting_an_operator_with_the_wrong_root_key_is_refused()
    {
        var response = await fixture.MintOperatorViaHttpAsync("not-the-real-root-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The root key mints and revokes operators; it must not itself be accepted as an operator key.</summary>
    [Fact]
    public async Task The_root_key_does_not_double_as_an_operator_key()
    {
        var response = await fixture.ProvisionTenantWithKeyAsync(ApiFixture.RootApiKeyForTests);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Two_operators_cannot_share_a_name()
    {
        var name = $"duplicate-operator-{Guid.CreateVersion7():N}";

        var first = await fixture.MintOperatorViaHttpAsync(ApiFixture.RootApiKeyForTests, name);
        var second = await fixture.MintOperatorViaHttpAsync(ApiFixture.RootApiKeyForTests, name);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>Listing operators is an audit surface: names and timestamps, never a key or its hash.</summary>
    [Fact]
    public async Task Listing_operators_never_exposes_a_key_or_its_hash()
    {
        await fixture.MintOperatorAsync();

        var body = (await fixture.ListOperatorsRawAsync()).ToLowerInvariant();

        body.ShouldNotContain("apikey");
        body.ShouldNotContain("keyhash");
    }
}
