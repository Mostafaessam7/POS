using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Api.Endpoints;
using POS.Identity.Authentication;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Inviting users into an existing tenant and granting/revoking their roles — the
/// surface HANDOVER.md's §9 flagged as missing: every provisioned tenant previously had
/// exactly one user, the seeded Owner, with no HTTP path to add a second person or grant
/// anyone a narrower role.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class UserManagementApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Inviting_a_user_returns_a_working_temporary_password()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.UserManage);
        using var _client = client;

        var response = await client.PostAsJsonAsync(
            "/api/v1/users", new InviteUserRequest($"invitee-{Guid.CreateVersion7():N}@example.test", "Invited Person"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var invited = (await response.Content.ReadFromJsonAsync<InvitedUser>())!;
        invited.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();

        // Proves the temp password is genuinely usable, not just a non-empty string:
        // hash it the same way login would and verify against what was persisted.
        var (hash, algorithm) = await fixture.ReadAsync<IdentityDbContext, (string, string)>(
            org.TenantId,
            async db =>
            {
                var user = await db.Users.FirstAsync(u => u.Id == invited.UserId);
                return (user.PasswordHash, user.PasswordAlgorithm);
            });

        var hasher = fixture.Services.GetRequiredService<IPasswordHasher>();
        hasher.Verify(invited.TemporaryPassword, hash, algorithm).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Inviting_a_duplicate_email_is_a_conflict()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.UserManage);
        using var _client = client;

        var request = new InviteUserRequest($"dup-{Guid.CreateVersion7():N}@example.test", "First");

        var first = await client.PostAsJsonAsync("/api/v1/users", request);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/v1/users", request with { DisplayName = "Second" });
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Listing_users_includes_the_seeded_owner_and_a_newly_invited_user()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.UserManage);
        using var _client = client;

        var invite = await client.PostAsJsonAsync(
            "/api/v1/users", new InviteUserRequest($"list-{Guid.CreateVersion7():N}@example.test", "Listed Person"));
        var invited = (await invite.Content.ReadFromJsonAsync<InvitedUser>())!;

        var response = await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var users = (await response.Content.ReadFromJsonAsync<PagedResponse<TestUserSummary>>())!.Items;
        users.ShouldContain(u => u.Id == invited.UserId);
    }

    [Fact]
    public async Task Creating_a_role_with_known_permissions_grants_exactly_those_codes()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.RoleManage, Permissions.Administration.UserManage);
        using var _client = client;

        var codes = new[] { Permissions.Catalog.ProductView, Permissions.Inventory.View };
        var response = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest($"custom-role-{Guid.CreateVersion7():N}", "A custom role", codes));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<RoleSummary>())!;
        created.PermissionCodes.OrderBy(c => c, StringComparer.Ordinal)
            .ShouldBe(codes.OrderBy(c => c, StringComparer.Ordinal));

        var list = (await (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<RoleSummary>>())!.Items;
        var listed = list.Single(r => r.Id == created.Id);
        listed.PermissionCodes.OrderBy(c => c, StringComparer.Ordinal)
            .ShouldBe(codes.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Creating_a_role_with_an_unknown_permission_code_is_rejected_and_creates_nothing()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.RoleManage, Permissions.Administration.UserManage);
        using var _client = client;

        var before = await (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<RoleSummary>>();

        var response = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest($"bad-role-{Guid.CreateVersion7():N}", "Has a bogus code", ["not.a.real.permission"]));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var after = await (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<RoleSummary>>();
        after!.Items.Count.ShouldBe(before!.Items.Count);
    }

    [Fact]
    public async Task Assigning_a_role_at_a_branch_the_caller_holds_UserManage_at_succeeds_and_is_visible()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, org.BranchId, Permissions.Administration.RoleManage, Permissions.Administration.UserManage);
        using var _client = client;

        var invite = await client.PostAsJsonAsync(
            "/api/v1/users", new InviteUserRequest($"assignee-{Guid.CreateVersion7():N}@example.test", "Assignee"));
        var invited = (await invite.Content.ReadFromJsonAsync<InvitedUser>())!;

        var roleResponse = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest($"assignable-role-{Guid.CreateVersion7():N}", "For assignment", [Permissions.Catalog.ProductView]));
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleSummary>())!;

        var assign = await client.PostAsJsonAsync(
            $"/api/v1/users/{invited.UserId}/roles",
            new AssignRoleRequest(role.Id, ScopeType.Branch, org.BranchId));

        assign.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var users = (await (await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<TestUserSummary>>())!.Items;
        var assignee = users.Single(u => u.Id == invited.UserId);
        assignee.RoleAssignments.ShouldContain(a =>
            a.RoleId == role.Id && a.ScopeType == "Branch" && a.ScopeId == org.BranchId);
    }

    [Fact]
    public async Task Assigning_a_role_at_a_branch_the_caller_does_not_hold_UserManage_at_is_forbidden()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        // Holds UserManage only at a DIFFERENT branch than org.BranchId.
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.CreateVersion7(), Permissions.Administration.UserManage);
        using var _client = client;

        var invite = await client.PostAsJsonAsync(
            "/api/v1/users", new InviteUserRequest($"scoped-{Guid.CreateVersion7():N}@example.test", "Scoped"));
        var invited = (await invite.Content.ReadFromJsonAsync<InvitedUser>())!;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/users/{invited.UserId}/roles",
            new AssignRoleRequest(Guid.CreateVersion7(), ScopeType.Branch, org.BranchId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoking_a_role_removes_it_from_the_assignment_list()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, org.BranchId, Permissions.Administration.RoleManage, Permissions.Administration.UserManage);
        using var _client = client;

        var invite = await client.PostAsJsonAsync(
            "/api/v1/users", new InviteUserRequest($"revokee-{Guid.CreateVersion7():N}@example.test", "Revokee"));
        var invited = (await invite.Content.ReadFromJsonAsync<InvitedUser>())!;

        var roleResponse = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest($"revocable-role-{Guid.CreateVersion7():N}", "For revocation", [Permissions.Catalog.ProductView]));
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleSummary>())!;

        (await client.PostAsJsonAsync(
            $"/api/v1/users/{invited.UserId}/roles",
            new AssignRoleRequest(role.Id, ScopeType.Branch, org.BranchId)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var revoke = await client.PostAsJsonAsync(
            $"/api/v1/users/{invited.UserId}/roles/revoke",
            new RevokeRoleRequest(role.Id, ScopeType.Branch, org.BranchId));

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var users = (await (await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<TestUserSummary>>())!.Items;
        var assignee = users.Single(u => u.Id == invited.UserId);
        assignee.RoleAssignments.ShouldNotContain(a =>
            a.RoleId == role.Id && a.ScopeType == "Branch" && a.ScopeId == org.BranchId);
    }

    [Fact]
    public async Task A_user_cannot_revoke_their_own_role_assignment()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, userId) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, org.BranchId, Permissions.Administration.RoleManage, Permissions.Administration.UserManage);
        using var _client = client;

        var roleResponse = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleRequest($"self-role-{Guid.CreateVersion7():N}", "Self assignment", [Permissions.Catalog.ProductView]));
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleSummary>())!;

        (await client.PostAsJsonAsync(
            $"/api/v1/users/{userId}/roles",
            new AssignRoleRequest(role.Id, ScopeType.Branch, org.BranchId)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/users/{userId}/roles/revoke",
            new RevokeRoleRequest(role.Id, ScopeType.Branch, org.BranchId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var users = (await (await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)))
            .Content.ReadFromJsonAsync<PagedResponse<TestUserSummary>>())!.Items;
        var self = users.Single(u => u.Id == userId);
        self.RoleAssignments.ShouldContain(a =>
            a.RoleId == role.Id && a.ScopeType == "Branch" && a.ScopeId == org.BranchId);
    }

    [Fact]
    public async Task Listing_permissions_returns_every_catalogue_code()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Administration.UserManage);
        using var _client = client;

        var response = await client.GetAsync(new Uri("/api/v1/permissions", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var permissions = (await response.Content.ReadFromJsonAsync<List<PermissionSummary>>())!;
        var codes = permissions.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

        foreach (var code in Permissions.AllCodes)
            codes.ShouldContain(code);
    }

    [Fact]
    public async Task Every_route_without_the_required_permission_is_forbidden()
    {
        var org = await fixture.ProvisionOrganisationAsync();
        using var client = fixture.CreateClientFor(org.TenantId);

        (await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync("/api/v1/users", new InviteUserRequest("nobody@example.test", "Nobody")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync(
            "/api/v1/roles", new CreateRoleRequest("nope", "nope", [])))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.GetAsync(new Uri("/api/v1/permissions", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Every_route_without_a_token_is_unauthorized()
    {
        using var client = fixture.CreateAnonymousClient();

        (await client.GetAsync(new Uri("/api/v1/users", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Enum members read back as strings (JsonStringEnumConverter, configured server-side
    // only via Program.cs's ConfigureHttpJsonOptions) — mirrors the string-typed response
    // DTOs PurchasingApiTests already uses for the same reason: the test client has no
    // matching converter, so a real UserSummary/RoleAssignmentSummary fails to deserialize.
    private sealed record TestUserSummary(
        Guid Id, string Email, string DisplayName, string Status, List<TestRoleAssignment> RoleAssignments);

    private sealed record TestRoleAssignment(Guid RoleId, string ScopeType, Guid ScopeId);
}
