using System.Net;
using System.Net.Http.Json;
using System.Linq;
using POS.Api.Endpoints;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Password-based login and refresh-token rotation over HTTP — the first real front
/// door for a human back-office user. Every provisioned tenant gets exactly one
/// authenticatable admin (an "Owner" role holding every permission) seeded at bootstrap,
/// so these tests provision their own tenant directly rather than going through
/// <see cref="ApiFixture.ProvisionOrganisationAsync"/>, to get at the full response
/// (subdomain, admin email) that the shared helper doesn't expose.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class AuthApiTests(ApiFixture fixture)
{
    private const string DefaultPassword = "ChangeMe123!";

    [Fact]
    public async Task The_seeded_admin_can_log_in_with_the_default_credentials()
    {
        var tenant = await ProvisionAsync();

        var response = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = DefaultPassword
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<LoginResponseDto>())!;

        body.AccessToken.ShouldNotBeNullOrWhiteSpace();
        body.TenantId.ShouldBe(tenant.Id);
        body.UserId.ShouldBe(tenant.AdminUserId);

        // The refresh token must never appear in the JSON body — only in an HttpOnly
        // Set-Cookie header, which is what actually carries it.
        ExtractRefreshCookie(response).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The whole point of seeding an Owner role: the token actually works against a real endpoint.</summary>
    [Fact]
    public async Task The_access_token_from_login_authorises_a_real_request()
    {
        var tenant = await ProvisionAsync();

        var loginResponse = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = DefaultPassword
        });

        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>())!;

        using var client = fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var productsResponse = await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative));

        productsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var tenant = await ProvisionAsync();

        var response = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = "not-the-real-password"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_subdomain_is_refused_with_the_same_shape_as_a_wrong_password()
    {
        // Same error either way — a caller must not be able to tell "no such
        // workspace" from "wrong password" apart, or the endpoint becomes a way to
        // enumerate which subdomains exist.
        var response = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = $"no-such-workspace-{Guid.CreateVersion7():N}",
            email = "admin@example.com",
            password = DefaultPassword
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Repeated_failed_logins_lock_the_account()
    {
        var tenant = await ProvisionAsync();
        using var client = fixture.CreateAnonymousClient();

        HttpResponseMessage? last = null;

        // User.RecordFailedLogin locks after 5 failures by default.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                subdomain = tenant.Subdomain,
                email = tenant.AdminEmail,
                password = "wrong-password"
            });
        }

        last!.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The 6th attempt, even with the CORRECT password, is refused — the account
        // is locked, not merely "still wrong".
        var lockedResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = DefaultPassword
        });

        lockedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await lockedResponse.Content.ReadAsStringAsync();
        body.ShouldContain("locked");
    }

    [Fact]
    public async Task A_refreshed_token_pair_authorises_a_real_request_and_the_old_refresh_token_stops_working()
    {
        var tenant = await ProvisionAsync();

        var loginResponse = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = DefaultPassword
        });

        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>())!;
        var originalCookie = ExtractRefreshCookie(loginResponse);

        using var refreshClient = fixture.CreateAnonymousClient();
        refreshClient.DefaultRequestHeaders.Add("Cookie", $"pos_refresh_token={originalCookie}");

        var refreshResponse = await refreshClient.PostAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), content: null);

        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refreshed = (await refreshResponse.Content.ReadFromJsonAsync<TokenResponseDto>())!;

        refreshed.AccessToken.ShouldNotBe(login.AccessToken);

        using var client = fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        (await client.GetAsync(new Uri("/api/v1/catalog/products", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The rotated-away token is single-use — presenting it again is reuse, which
        // RefreshTokenService treats as a possible theft and refuses.
        using var reuseClient = fixture.CreateAnonymousClient();
        reuseClient.DefaultRequestHeaders.Add("Cookie", $"pos_refresh_token={originalCookie}");

        var reuseResponse = await reuseClient.PostAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), content: null);

        reuseResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refreshing_without_a_cookie_is_refused()
    {
        var response = await fixture.CreateAnonymousClient().PostAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logging_out_revokes_the_refresh_token()
    {
        var tenant = await ProvisionAsync();

        var loginResponse = await fixture.CreateAnonymousClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            subdomain = tenant.Subdomain,
            email = tenant.AdminEmail,
            password = DefaultPassword
        });

        var cookie = ExtractRefreshCookie(loginResponse);

        using var logoutClient = fixture.CreateAnonymousClient();
        logoutClient.DefaultRequestHeaders.Add("Cookie", $"pos_refresh_token={cookie}");

        var logoutResponse = await logoutClient.PostAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative), content: null);

        logoutResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var refreshClient = fixture.CreateAnonymousClient();
        refreshClient.DefaultRequestHeaders.Add("Cookie", $"pos_refresh_token={cookie}");

        var refreshResponse = await refreshClient.PostAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative), content: null);

        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logging_out_without_a_session_is_a_no_op()
    {
        var response = await fixture.CreateAnonymousClient().PostAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>Pulls the refresh token's plaintext value out of the response's Set-Cookie header.</summary>
    private static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("pos_refresh_token=", StringComparison.Ordinal));

        var valuePart = setCookie.Split(';')[0];
        return valuePart["pos_refresh_token=".Length..];
    }

    private async Task<ProvisionedTenantDto> ProvisionAsync()
    {
        var (_, operatorKey) = await fixture.MintOperatorAsync();

        using var client = fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireOperatorApiKeyFilter.HeaderName, operatorKey);

        var response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            name = $"Tenant {Guid.CreateVersion7():N}"[..24]
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ProvisionedTenantDto>())!;
    }

    private sealed record ProvisionedTenantDto(
        Guid Id, Guid CompanyId, Guid BranchId, Guid WarehouseId, Guid? ProvisionedByOperatorId,
        string Subdomain, Guid AdminUserId, string AdminEmail);

    private sealed record LoginResponseDto(
        string AccessToken, DateTimeOffset ExpiresAt,
        Guid TenantId, Guid UserId, string DisplayName, string Email);

    private sealed record TokenResponseDto(string AccessToken, DateTimeOffset ExpiresAt);
}
