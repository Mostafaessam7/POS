using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Common.Security;
using POS.Common.Tenancy;
using POS.Common.Validation;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.Inventory;
using POS.Purchasing;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// Reads and writes a tenant's own overrides of deployment-wide policy defaults.
/// </summary>
/// <remarks>
/// <para>
/// This is the write side <see cref="POS.Contracts.Identity.ITenantSettingsDirectory"/>
/// deliberately does not expose (see its remarks) — the module that owns a policy
/// SHAPE (Purchasing, Inventory) resolves its own settings key through the read-only
/// contract, but the module that owns WRITING an override is Identity, because
/// <see cref="TenantSetting"/> lives there and the write has no policy-specific
/// meaning to Identity at all: it is always exactly "upsert this tenant's (key,
/// value)". Composing the two into a per-policy-area endpoint pair is therefore a
/// host concern, the same reasoning that makes <c>ReconciliationEndpoints</c>
/// host-assembled — this is the one place that legitimately sees both a domain
/// policy shape (from Purchasing/Inventory) and the generic settings store (from
/// Identity).
/// </para>
/// <para>
/// Every route here is a MERCHANT operation on a tenant that already exists — like
/// <c>POST /terminals</c>, not like <c>POST /tenants</c> — so it sits behind ordinary
/// <c>RequireAuthorization()</c> plus <c>Permissions.Administration.SettingsEdit</c>,
/// never the provisioning operator gate. There is no narrower scope than "the whole
/// tenant" for a policy that spans every branch and warehouse a tenant has, so this
/// is permission-gated only, with no <see cref="IPermissionScopeGuard"/> check — the
/// same shape a genuinely tenant-wide operation takes elsewhere in this codebase.
/// </para>
/// </remarks>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/settings").RequireAuthorization();

        group.MapGet("/purchasing-policy", async (
            ITenantContext tenant,
            PurchasingPolicyResolver resolver,
            CancellationToken ct) =>
        {
            var policy = await resolver.ResolveAsync(tenant.TenantId, ct);
            return Results.Ok(policy);
        })
        .RequirePermission(Permissions.Administration.SettingsEdit);

        group.MapPut("/purchasing-policy", async (
            // [FromBody] is NOT decorative here. PurchasingPolicyOptions is ALSO
            // registered as a DI singleton (the deployment default other endpoints
            // inject as a service) — without this attribute, minimal API's binder
            // sees a type resolvable from the container and silently resolves THAT
            // instead of reading the request body, so a PUT would appear to succeed
            // while saving whatever the deployment default already was, regardless
            // of what the caller sent.
            [FromBody] PurchasingPolicyOptions request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IdentityDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            await UpsertAsync(db, tenant.TenantId, PurchasingPolicyResolver.SettingKey, request, currentUser, clock, ct);
            return Results.Ok(request);
        })
        .AddValidation<PurchasingPolicyOptions>()
        .RequirePermission(Permissions.Administration.SettingsEdit);

        group.MapGet("/inventory-policy", async (
            ITenantContext tenant,
            InventoryPolicyResolver resolver,
            CancellationToken ct) =>
        {
            var policy = await resolver.ResolveAsync(tenant.TenantId, ct);
            return Results.Ok(policy);
        })
        .RequirePermission(Permissions.Administration.SettingsEdit);

        group.MapPut("/inventory-policy", async (
            // [FromBody] required for the same reason as the purchasing-policy PUT
            // above — InventoryPolicyOptions is also DI-registered as a singleton.
            [FromBody] InventoryPolicyOptions request,
            ITenantContext tenant,
            ICurrentUser currentUser,
            IdentityDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            await UpsertAsync(db, tenant.TenantId, InventoryPolicyResolver.SettingKey, request, currentUser, clock, ct);
            return Results.Ok(request);
        })
        .AddValidation<InventoryPolicyOptions>()
        .RequirePermission(Permissions.Administration.SettingsEdit);

        return app;
    }

    /// <remarks>
    /// "Setting" is always an upsert — a tenant has at most one override per key —
    /// so every writer shares this one path rather than each duplicating the
    /// find-or-create dance.
    /// </remarks>
    private static async Task UpsertAsync<TPolicy>(
        IdentityDbContext db,
        Guid tenantId,
        string key,
        TPolicy request,
        ICurrentUser currentUser,
        IClock clock,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request);
        var userId = currentUser.UserId ?? Guid.Empty;

        var existing = await db.TenantSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key, ct);

        if (existing is null)
            db.TenantSettings.Add(TenantSetting.Create(tenantId, key, json, userId, clock.UtcNow));
        else
            existing.Update(json, userId, clock.UtcNow);

        await db.SaveChangesAsync(ct);
    }
}

public sealed class PurchasingPolicyOptionsValidator : AbstractValidator<PurchasingPolicyOptions>
{
    public PurchasingPolicyOptionsValidator()
    {
        RuleFor(r => r.ApprovalRequiredAbove).GreaterThanOrEqualTo(0m);
        RuleFor(r => r.ReceiptTolerancePercentage).GreaterThanOrEqualTo(0m);
        RuleFor(r => r.ReceiptToleranceUnits).GreaterThanOrEqualTo(0m);

        RuleForEach(r => r.Thresholds).ChildRules(threshold =>
        {
            threshold.RuleFor(t => t.FromValue).GreaterThanOrEqualTo(0m);
            threshold.RuleFor(t => t.Level).IsInEnum();
        });
    }
}

public sealed class InventoryPolicyOptionsValidator : AbstractValidator<InventoryPolicyOptions>
{
    public InventoryPolicyOptionsValidator() =>
        RuleForEach(r => r.VarianceWriteOffThresholds).ChildRules(threshold =>
        {
            threshold.RuleFor(t => t.FromValue).GreaterThanOrEqualTo(0m);
            threshold.RuleFor(t => t.Level).IsInEnum();
        });
}
