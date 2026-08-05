using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Common.Validation;
using POS.Identity.Authentication;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// Platform provisioning: minting the operator identities allowed to bootstrap a
/// tenant, and the bootstrap itself — creating a tenant and enrolling a terminal.
/// </summary>
/// <remarks>
/// <para>
/// Creating a tenant is genuinely different from every other write in this system: it
/// necessarily happens before a tenant exists, so it runs under an explicit system
/// bypass rather than pretending a tenant context is available. That bypass is logged,
/// which is the whole point of making it awkward to reach (ADR 006).
/// </para>
/// <para>
/// There is still no platform-operator ROLE in the permission catalogue — every
/// permission in <c>Permissions.cs</c> is merchant-scoped, and a merchant permission
/// cannot gate an operation performed before any merchant (tenant) exists. Building a
/// tenant-less identity — a new user store, a token shape without a
/// <c>tenant_id</c> claim, and carve-outs in <see cref="TenantResolutionMiddleware"/>
/// for it — remains judged a disproportionate amount of new surface for what this
/// needs: a way for the platform's own operations tooling to bootstrap a tenant
/// safely, with an individually revocable identity behind each call rather than one
/// secret everyone with access shares. So <c>POST /tenants</c> is gated by a NAMED
/// <see cref="ProvisioningOperator"/> credential (<see cref="RequireOperatorApiKeyFilter"/>),
/// hashed at rest the same way a refresh token is, checked in constant time, and
/// REFUSED CLOSED if the presented key matches no active operator. Operators are
/// themselves minted and revoked through <c>/provisioning/operators</c>, gated by a
/// SEPARATE root secret (<see cref="RequireRootOperatorApiKeyFilter"/>) that is
/// deliberately harder to reach day to day — it mints and revokes operator
/// identities, but never itself provisions a tenant.
/// </para>
/// <para>
/// <c>POST /terminals</c> and <c>GET /tenants/{id}/settings</c> need no such gate: they
/// already run under <c>RequireAuthorization()</c>, i.e. an ordinary tenant-scoped
/// bearer token, because enrolling a terminal or reading settings is a MERCHANT
/// operation performed inside a tenant that already exists, not a platform bootstrap
/// step.
/// </para>
/// </remarks>
public static class ProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapProvisioningEndpoints(
        this IEndpointRouteBuilder app,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!enabled)
            return app;

        var group = app.MapGroup("/api/v1").AllowAnonymous().RequireRateLimiting("provisioning");

        group.MapPost("/tenants", async (
            CreateTenantRequest request,
            HttpContext httpContext,
            IdentityDbContext db,
            ITenantContext tenantContext,
            IPasswordHasher hasher,
            IClock clock,
            CancellationToken ct) =>
        {
            var operatorId = httpContext.Items[RequireOperatorApiKeyFilter.OperatorIdItemKey] as Guid?;

            var tenant = Tenant.Create(request.Name, Slugify(request.Name), operatorId);
            db.Tenants.Add(tenant);

            // EnterTenant, not BypassForSystemOperation. A bypass would suspend
            // filtering but name no tenant, so the write interceptor would have nothing
            // to stamp onto the company, branch and warehouse below and all three would
            // be persisted with an empty TenantId — belonging to nobody and invisible
            // to every later query. Tenant itself is the one table that is not
            // tenant-scoped, so it is added before the scope opens.
            using var _ = tenantContext.EnterTenant(tenant.Id, "tenant provisioning");

            // A tenant with no company, branch or warehouse cannot transact: a sale
            // needs a branch, stock needs a warehouse, and a fiscal document needs a
            // company. Provisioning them together is what makes the tenant usable
            // rather than merely present.
            var company = Company.Create(
                request.Name,
                request.Name,
                taxRegistration: "PENDING",
                baseCurrency: request.Currency,
                countryCode: request.CountryCode);

            var branch = Branch.Create(company.Id, "MAIN", "Main Branch", "UTC", businessDayStartHour: 0);
            var warehouse = Warehouse.Create(branch.Id, "MAIN", "Sales Floor", WarehouseKind.SalesFloor);

            db.Companies.Add(company);
            db.Branches.Add(branch);
            db.Warehouses.Add(warehouse);

            // A tenant nobody can log into is not usable, so provisioning also seeds
            // exactly one thing a human can authenticate as: an Owner role holding
            // EVERY permission in the catalogue (Permissions.AllCodes), and one admin
            // user granted that role tenant-wide (ScopeType.Company at Guid.Empty,
            // which PermissionSet.Has treats as "everywhere" — the same convention
            // ApiFixture's test seeding already relies on). Permissions themselves are
            // platform reference data (see PermissionConfiguration's remarks), so they
            // are inserted here only if this is the very first tenant provisioned
            // against a fresh database.
            var allPermissions = await db.Permissions
                .Where(p => Permissions.AllCodes.Contains(p.Code))
                .ToListAsync(ct);

            var missingCodes = Permissions.AllCodes.Except(allPermissions.Select(p => p.Code), StringComparer.Ordinal);

            foreach (var code in missingCodes)
            {
                var permission = Permission.Define(code, code.Split('.')[0], $"Seeded at tenant bootstrap: {code}");
                db.Permissions.Add(permission);
                allPermissions.Add(permission);
            }

            var ownerRole = Role.Create("Owner", "Full access — created automatically at tenant bootstrap.", isSystemRole: true);
            foreach (var permission in allPermissions)
                ownerRole.Grant(permission.Id);

            db.Roles.Add(ownerRole);

            var hashed = hasher.Hash(request.AdminPassword);
            var adminUser = User.Create(request.AdminEmail, "Administrator", hashed.Hash, hashed.Algorithm);
            adminUser.AssignRole(ownerRole.Id, ScopeType.Company, Guid.Empty);

            db.Users.Add(adminUser);

            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/tenants/{tenant.Id}",
                new ProvisionedTenant(
                    tenant.Id, company.Id, branch.Id, warehouse.Id, tenant.ProvisionedByOperatorId,
                    tenant.Subdomain, adminUser.Id, adminUser.Email));
        })
        .AddValidation<CreateTenantRequest>()
        .AddEndpointFilter<RequireOperatorApiKeyFilter>();

        MapOperatorManagement(group);

        group.MapPost("/terminals", async (
            EnrolTerminalRequest request,
            IdentityDbContext db,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            if (!tenantContext.IsResolved)
                return Error.Forbidden("identity.tenant_required", "A tenant is required.").ToHttpResult();

            var branch = await db.Branches
                .OrderBy(b => b.Code)
                .FirstOrDefaultAsync(ct);

            if (branch is null)
                return Error.NotFound("identity.branch.none", "The tenant has no branch to enrol against.").ToHttpResult();

            // The thumbprint identifies the physical device (ADR 014). A real
            // enrolment presents a client certificate; this generates a placeholder
            // because certificate issuance is a deployment concern with no server-side
            // implementation here.
            var terminal = Terminal.Enrol(
                branch.Id,
                request.Code,
                request.Name,
                certificateThumbprint: Guid.CreateVersion7().ToString("N"));

            db.Terminals.Add(terminal);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/terminals/{terminal.Id}", new CreatedResource(terminal.Id));
        })
        .RequireAuthorization();

        // Exists so the tenant-mismatch rule is observable. The middleware compares the
        // route's tenantId against the token's and answers 404 — not 403 — because a
        // 403 confirms the resource exists, which is itself a leak across a security
        // boundary.
        group.MapGet("/tenants/{tenantId:guid}/settings", (
            Guid tenantId,
            ITenantContext tenantContext) =>
            tenantContext.IsResolved && tenantContext.TenantId == tenantId
                ? Results.Ok(new { tenantId })
                : Results.NotFound())
        .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Mints and revokes the named <see cref="ProvisioningOperator"/> identities that
    /// <c>POST /tenants</c> checks against.
    /// </summary>
    /// <remarks>
    /// Gated by <see cref="RequireRootOperatorApiKeyFilter"/>, a secret deliberately
    /// separate from any individual operator's key: the root key's only job is to
    /// mint and revoke operator identities, so it can be locked away far more tightly
    /// (used once per new hire, say) than the day-to-day keys those operators then use.
    /// </remarks>
    private static void MapOperatorManagement(IEndpointRouteBuilder group)
    {
        var operators = group.MapGroup("/provisioning/operators")
            .AddEndpointFilter<RequireRootOperatorApiKeyFilter>();

        operators.MapPost("/", async (
            CreateProvisioningOperatorRequest request,
            IdentityDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var plaintextKey = GenerateOperatorKey();

            var op = ProvisioningOperator.Enrol(
                request.Name, ProvisioningOperator.HashKey(plaintextKey), clock.UtcNow);

            db.ProvisioningOperators.Add(op);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Matches(ex))
            {
                return Error.Conflict(
                    "provisioning.operator_name_taken",
                    "An operator with this name already exists.").ToHttpResult();
            }

            // THE ONLY MOMENT THE PLAINTEXT KEY EXISTS OUTSIDE THE CALLER'S HANDS. Only
            // the hash is persisted; a lost key cannot be recovered, only replaced by
            // revoking this operator and minting a new one.
            return Results.Created(
                $"/api/v1/provisioning/operators/{op.Id}",
                new ProvisionedOperator(op.Id, op.Name, plaintextKey, op.CreatedAt));
        })
        .AddValidation<CreateProvisioningOperatorRequest>();

        // Names and timestamps only — never a hash, let alone a key. This is the
        // audit-visibility surface, not a credential store.
        operators.MapGet("/", async (IdentityDbContext db, CancellationToken ct) =>
        {
            var all = await db.ProvisioningOperators
                .AsNoTracking()
                .OrderBy(o => o.CreatedAt)
                .Select(o => new ProvisioningOperatorSummary(o.Id, o.Name, o.CreatedAt, o.RevokedAt))
                .ToListAsync(ct);

            return Results.Ok(all);
        });

        operators.MapPost("/{id:guid}/revoke", async (
            Guid id,
            IdentityDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var op = await db.ProvisioningOperators.FirstOrDefaultAsync(o => o.Id == id, ct);

            if (op is null)
                return Results.NotFound();

            // Idempotent — ProvisioningOperator.Revoke does not move an
            // already-recorded RevokedAt, so calling this twice is harmless.
            op.Revoke(clock.UtcNow);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ProvisioningOperatorSummary(op.Id, op.Name, op.CreatedAt, op.RevokedAt));
        });
    }

    private static string GenerateOperatorKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string Slugify(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);

        // Subdomains are globally unique across the platform, so a suffix is added
        // rather than risking a collision between two merchants of the same name.
        return $"{(slug.Length == 0 ? "tenant" : slug)}-{Guid.CreateVersion7().ToString("N")[..8]}";
    }
}

public sealed record CreateTenantRequest(
    string Name,
    string Currency = "USD",
    string CountryCode = "US",
    string AdminEmail = "admin@example.com",
    string AdminPassword = "ChangeMe123!");

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.CountryCode).NotEmpty().Length(2);
        RuleFor(r => r.AdminEmail).NotEmpty().EmailAddress();
        RuleFor(r => r.AdminPassword).NotEmpty().MinimumLength(8);
    }
}

public sealed record EnrolTerminalRequest(string Code, string Name);

public sealed record ProvisionedTenant(
    Guid Id,
    Guid CompanyId,
    Guid BranchId,
    Guid WarehouseId,
    Guid? ProvisionedByOperatorId,
    string Subdomain,
    Guid AdminUserId,
    string AdminEmail);

public sealed record CreatedResource(Guid Id);

public sealed record CreateProvisioningOperatorRequest(string Name);

public sealed class CreateProvisioningOperatorRequestValidator : AbstractValidator<CreateProvisioningOperatorRequest>
{
    public CreateProvisioningOperatorRequestValidator() =>
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
}

/// <summary>The one response that carries the plaintext key — returned once, at creation, never again.</summary>
public sealed record ProvisionedOperator(Guid Id, string Name, string ApiKey, DateTimeOffset CreatedAt);

public sealed record ProvisioningOperatorSummary(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Gates <c>POST /tenants</c> behind a named, individually revocable operator
/// identity instead of a permission, because the operation it protects happens
/// before any tenant — and therefore any tenant-scoped user or role — exists to
/// grant one.
/// </summary>
/// <remarks>
/// <para>
/// REFUSES CLOSED: if the presented key matches no active <see cref="ProvisioningOperator"/>
/// — including when the operator table is empty, which is the default state of a
/// fresh deployment — every caller gets 403 rather than the endpoint falling open.
/// </para>
/// <para>
/// Comparison of the presented key happens by HASHING it and looking up the hash
/// (<see cref="ProvisioningOperator.HashKey"/>), never by comparing plaintext against
/// a stored plaintext — there is no stored plaintext to compare against, the same
/// stance <see cref="RefreshToken"/> takes. The lookup itself is an equality match on
/// an indexed hash column, not a loop with a timing-sensitive comparison, because
/// there is nothing secret being compared byte-by-byte: two different keys either
/// hash to the same value (never, in practice) or they do not, and the row lookup
/// itself is what varies in time, not this filter's own logic.
/// </para>
/// <para>
/// On a match, the matched operator's id is attached to <c>HttpContext.Items</c> so
/// the handler can attribute the tenant it is about to create — see
/// <see cref="OperatorIdItemKey"/>.
/// </para>
/// </remarks>
public sealed class RequireOperatorApiKeyFilter(IdentityDbContext db) : IEndpointFilter
{
    public const string HeaderName = "X-Operator-Key";

    /// <summary>The <c>HttpContext.Items</c> key the matched operator's id is attached under.</summary>
    public const string OperatorIdItemKey = "ProvisioningOperatorId";

    private static readonly Error MissingOrInvalid = Error.Forbidden(
        "provisioning.operator_key_invalid",
        "A valid operator key is required to provision a tenant.");

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var providedKey = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(providedKey))
            return MissingOrInvalid.ToHttpResult();

        var hash = ProvisioningOperator.HashKey(providedKey);

        // IgnoreQueryFilters is not needed: ProvisioningOperator is not ITenantScoped
        // (see its remarks), so no tenant filter would ever apply to it — there is no
        // tenant yet for this call to belong to.
        var matched = await db.ProvisioningOperators
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.KeyHash == hash, context.HttpContext.RequestAborted);

        if (matched is null || !matched.IsActive)
            return MissingOrInvalid.ToHttpResult();

        context.HttpContext.Items[OperatorIdItemKey] = matched.Id;

        return await next(context);
    }
}

/// <summary>
/// Gates <c>/provisioning/operators</c> — the endpoints that mint and revoke the
/// identities <see cref="RequireOperatorApiKeyFilter"/> checks.
/// </summary>
/// <remarks>
/// A separate secret from any individual operator key, by design: this one is used
/// far less often (onboarding or offboarding an operator, not day-to-day
/// provisioning), so it can live somewhere harder to reach without slowing down
/// routine bootstrap calls. Refuses closed if unconfigured, same stance the previous
/// single-shared-secret design took — an unconfigured secret is the default state of
/// a fresh deployment, not an edge case.
/// </remarks>
public sealed class RequireRootOperatorApiKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    public const string HeaderName = "X-Provisioning-Root-Key";

    private static readonly Error MissingOrInvalid = Error.Forbidden(
        "provisioning.root_key_invalid",
        "A valid root provisioning key is required to manage operators.");

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var configuredKey = configuration["Provisioning:RootApiKey"];

        if (string.IsNullOrEmpty(configuredKey))
            return MissingOrInvalid.ToHttpResult();

        var providedKey = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(providedKey)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedKey), Encoding.UTF8.GetBytes(configuredKey)))
        {
            return MissingOrInvalid.ToHttpResult();
        }

        return await next(context);
    }
}
