using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Persistence;
using POS.Common.Security;
using POS.Common.Validation;
using POS.Identity.Authentication;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// Inviting users into a tenant that already exists, and granting/revoking the roles
/// that determine what they can do — the surface HANDOVER.md's §9 flagged as missing:
/// every tenant provisioned via <c>POST /tenants</c> previously had exactly one user,
/// the seeded Owner, with no way to add a second person or grant anyone a narrower role.
/// </summary>
/// <remarks>
/// <para>
/// There is no invite/email flow anywhere in this codebase, so "inviting" a user here
/// means minting a temporary password server-side and returning it exactly once in the
/// response — the same "plaintext exists only in the creation response, never persisted"
/// stance <c>POST /provisioning/operators</c> already takes toward an operator key.
/// </para>
/// <para>
/// Assigning/revoking a role takes an arbitrary scope in the request body, so both
/// routes use the codebase's established two-step gate: a coarse
/// <c>RequirePermission(Administration.UserManage)</c> (holds it at ANY scope) plus an
/// in-handler <see cref="IPermissionScopeGuard"/> check against the REQUEST's actual
/// scope — omitting the second step would let an admin at branch A grant or revoke
/// roles at branch B.
/// </para>
/// <para>
/// Revoking a role a user holds on THEMSELVES is refused — the same separation-of-duties
/// stance approval ladders already take (<c>AllowSelfApproval</c> refused by default):
/// nobody should be able to strip their own access unilaterally.
/// </para>
/// </remarks>
public static class UserManagementEndpoints
{
    private static readonly Error ScopeDenied = Error.Forbidden(
        "users.scope_denied", "You do not hold this permission at the requested scope.");

    private static readonly Error CannotRevokeOwnRole = Error.Forbidden(
        "users.cannot_revoke_own_role", "You cannot revoke your own role assignment.");

    public static IEndpointRouteBuilder MapUserManagementEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1").RequireAuthorization();

        group.MapGet("/users", async (
            IdentityDbContext db,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
        {
            var (effectivePage, effectivePageSize) = NormalisePaging(page, pageSize);

            var query = db.Users.AsNoTracking().OrderBy(u => u.Email);
            var totalCount = await query.CountAsync(ct);

            var users = await query
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .Select(u => new UserSummary(
                    u.Id,
                    u.Email,
                    u.DisplayName,
                    u.Status,
                    u.RoleAssignments
                        .Select(a => new RoleAssignmentSummary(a.RoleId, a.ScopeType, a.ScopeId))
                        .ToList()))
                .ToListAsync(ct);

            // RoleAssignmentSummary carries a role id only; the client resolves names
            // against GET /roles, the same "one source of truth per shape" pattern
            // GET /organization already uses for company/branch/warehouse names.
            return Results.Ok(new PagedResponse<UserSummary>(users, effectivePage, effectivePageSize, totalCount));
        })
        .RequirePermission(Permissions.Administration.UserManage);

        group.MapPost("/users", async (
            InviteUserRequest request,
            IdentityDbContext db,
            IPasswordHasher hasher,
            CancellationToken ct) =>
        {
            var temporaryPassword = GenerateTemporaryPassword();
            var hashed = hasher.Hash(temporaryPassword);
            var user = User.Create(request.Email, request.DisplayName, hashed.Hash, hashed.Algorithm);

            db.Users.Add(user);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                return Error.Conflict(
                    "users.email_taken", "A user with this email already exists.").ToHttpResult();
            }

            // THE ONLY MOMENT THE PLAINTEXT PASSWORD EXISTS OUTSIDE THE CALLER'S HANDS —
            // only the hash is persisted, mirroring ProvisionedOperator's ApiKey.
            return Results.Created(
                $"/api/v1/users/{user.Id}",
                new InvitedUser(user.Id, user.Email, temporaryPassword));
        })
        .AddValidation<InviteUserRequest>()
        .RequirePermission(Permissions.Administration.UserManage);

        group.MapPost("/users/{id:guid}/roles", async (
            Guid id,
            AssignRoleRequest request,
            IdentityDbContext db,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            if (!await scope.HasAtScopeAsync(Permissions.Administration.UserManage, request.ScopeId, ct))
                return ScopeDenied.ToHttpResult();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

            if (user is null)
                return Results.NotFound();

            var roleExists = await db.Roles.AnyAsync(r => r.Id == request.RoleId, ct);

            if (!roleExists)
                return Error.NotFound("users.role.unknown", "The role does not exist.").ToHttpResult();

            var beforeCount = user.RoleAssignments.Count;
            user.AssignRole(request.RoleId, request.ScopeType, request.ScopeId);

            // User was LOADED, not newly Add()-ed, so EF's change detection cannot tell
            // a client-generated key it has never seen apart from "already exists" —
            // its default heuristic marks a new owned-collection entry Modified, which
            // issues an UPDATE against a row that was never inserted (0 rows affected,
            // reported as a concurrency conflict). Same bug and fix as
            // StocktakeService.RecordCountAsync — see HANDOVER.md §1.
            if (user.RoleAssignments.Count > beforeCount)
                db.Entry(user.RoleAssignments[^1]).State = EntityState.Added;

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
        .AddValidation<AssignRoleRequest>()
        .RequirePermission(Permissions.Administration.UserManage);

        group.MapPost("/users/{id:guid}/roles/revoke", async (
            Guid id,
            RevokeRoleRequest request,
            IdentityDbContext db,
            ICurrentUser currentUser,
            IPermissionScopeGuard scope,
            CancellationToken ct) =>
        {
            if (currentUser.UserId == id)
                return CannotRevokeOwnRole.ToHttpResult();

            if (!await scope.HasAtScopeAsync(Permissions.Administration.UserManage, request.ScopeId, ct))
                return ScopeDenied.ToHttpResult();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

            if (user is null)
                return Results.NotFound();

            user.RevokeRole(request.RoleId, request.ScopeType, request.ScopeId);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
        .AddValidation<RevokeRoleRequest>()
        .RequirePermission(Permissions.Administration.UserManage);

        group.MapGet("/roles", async (
            IdentityDbContext db,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
        {
            var (effectivePage, effectivePageSize) = NormalisePaging(page, pageSize);

            var permissionsByCode = await db.Permissions
                .AsNoTracking()
                .ToDictionaryAsync(p => p.Id, p => p.Code, ct);

            var query = db.Roles.AsNoTracking().OrderBy(r => r.Name);
            var totalCount = await query.CountAsync(ct);

            var roles = await query
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToListAsync(ct);

            var items = roles.Select(r => new RoleSummary(
                r.Id,
                r.Name,
                r.Description,
                r.IsSystemRole,
                r.PermissionIds
                    .Where(permissionsByCode.ContainsKey)
                    .Select(pid => permissionsByCode[pid])
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToList()))
                .ToList();

            return Results.Ok(new PagedResponse<RoleSummary>(items, effectivePage, effectivePageSize, totalCount));
        })
        .RequirePermission(Permissions.Administration.UserManage);

        group.MapPost("/roles", async (
            CreateRoleRequest request,
            IdentityDbContext db,
            CancellationToken ct) =>
        {
            var permissions = await db.Permissions
                .Where(p => request.PermissionCodes.Contains(p.Code))
                .ToListAsync(ct);

            var unknown = request.PermissionCodes.Except(permissions.Select(p => p.Code), StringComparer.Ordinal).ToList();

            if (unknown.Count > 0)
            {
                return Error.NotFound(
                    "users.permission.unknown",
                    $"Unknown permission code(s): {string.Join(", ", unknown)}").ToHttpResult();
            }

            var role = Role.Create(request.Name, request.Description, isSystemRole: false);

            foreach (var permission in permissions)
                role.Grant(permission.Id);

            db.Roles.Add(role);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                return Error.Conflict(
                    "users.role_name_taken", "A role with this name already exists.").ToHttpResult();
            }

            return Results.Created(
                $"/api/v1/roles/{role.Id}",
                new RoleSummary(role.Id, role.Name, role.Description, role.IsSystemRole, request.PermissionCodes));
        })
        .AddValidation<CreateRoleRequest>()
        .RequirePermission(Permissions.Administration.RoleManage);

        group.MapGet("/permissions", async (IdentityDbContext db, CancellationToken ct) =>
        {
            var permissions = await db.Permissions
                .AsNoTracking()
                .OrderBy(p => p.Module).ThenBy(p => p.Code)
                .Select(p => new PermissionSummary(p.Code, p.Module, p.Description))
                .ToListAsync(ct);

            return Results.Ok(permissions);
        })
        .RequirePermission(Permissions.Administration.UserManage);

        return app;
    }

    /// <remarks>
    /// A 20-character alphanumeric-plus-symbol string via .NET's own constant-time
    /// random string generator — strong enough for a one-time credential that is
    /// hashed immediately and shown to the caller exactly once, same trust level as
    /// <c>ProvisioningEndpoints.GenerateOperatorKey</c>.
    /// </remarks>
    private static string GenerateTemporaryPassword() =>
        RandomNumberGenerator.GetString(
            "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%",
            20);

    /// <summary>
    /// Clamps caller-supplied paging into a sane range rather than trusting it.
    /// </summary>
    /// <remarks>
    /// <c>GET /users</c> and <c>GET /roles</c> used to return every row, unpaged —
    /// fine at the scale a handful of provisioned users sits at, flagged in
    /// PROJECT_STATUS.md as the one thing that would need fixing before a real
    /// tenant with hundreds of users showed up. A page/pageSize the caller controls
    /// entirely, with no upper bound, would just move the same problem one query
    /// parameter away — a client (or a bug) asking for pageSize=999999 gets exactly
    /// the unpaged query back. Capping it here is the actual fix.
    /// </remarks>
    private static (int Page, int PageSize) NormalisePaging(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize is < 1 or > 100 ? 20 : pageSize);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record InviteUserRequest(string Email, string DisplayName);

public sealed class InviteUserRequestValidator : AbstractValidator<InviteUserRequest>
{
    public InviteUserRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress();
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(200);
    }
}

/// <summary>The one response that carries the plaintext temporary password — returned once, at invite time, never again.</summary>
public sealed record InvitedUser(Guid UserId, string Email, string TemporaryPassword);

public sealed record RoleAssignmentSummary(Guid RoleId, ScopeType ScopeType, Guid ScopeId);

public sealed record UserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    UserStatus Status,
    IReadOnlyList<RoleAssignmentSummary> RoleAssignments);

public sealed record AssignRoleRequest(Guid RoleId, ScopeType ScopeType, Guid ScopeId);

public sealed class AssignRoleRequestValidator : AbstractValidator<AssignRoleRequest>
{
    public AssignRoleRequestValidator()
    {
        RuleFor(r => r.RoleId).NotEmpty();
        RuleFor(r => r.ScopeType).IsInEnum();
    }
}

public sealed record RevokeRoleRequest(Guid RoleId, ScopeType ScopeType, Guid ScopeId);

public sealed class RevokeRoleRequestValidator : AbstractValidator<RevokeRoleRequest>
{
    public RevokeRoleRequestValidator()
    {
        RuleFor(r => r.RoleId).NotEmpty();
        RuleFor(r => r.ScopeType).IsInEnum();
    }
}

public sealed record RoleSummary(
    Guid Id,
    string Name,
    string Description,
    bool IsSystemRole,
    IReadOnlyList<string> PermissionCodes);

public sealed record CreateRoleRequest(string Name, string Description, IReadOnlyList<string> PermissionCodes);

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Description).NotEmpty().MaximumLength(500);
        RuleFor(r => r.PermissionCodes).NotNull();
    }
}

public sealed record PermissionSummary(string Code, string Module, string Description);
