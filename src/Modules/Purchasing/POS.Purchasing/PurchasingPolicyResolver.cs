using System.Text.Json;
using POS.Contracts.Identity;

namespace POS.Purchasing;

/// <summary>
/// Resolves the EFFECTIVE purchasing policy for a tenant: its own stored override if
/// it has set one, otherwise this deployment's default.
/// </summary>
/// <remarks>
/// <see cref="PurchasingPolicyOptions"/>'s own remarks name this resolver as the
/// contained follow-up once a tenant settings store existed (ADR 049) — every caller
/// already goes through <c>PurchasingPolicyOptions</c> rather than reading
/// configuration directly, so swapping "the deployment default" for "this tenant's
/// effective policy" changes call sites, not the policy shape itself.
///
/// A malformed or unreadable override falls back to the deployment default rather
/// than throwing: a stored setting that fails to deserialize should degrade to "no
/// override" behaviour, the same as if it had never been set, not take down every
/// purchasing operation for that tenant.
/// </remarks>
public sealed class PurchasingPolicyResolver(PurchasingPolicyOptions defaultPolicy, ITenantSettingsDirectory settings)
{
    public const string SettingKey = "purchasing.policy";

    public async Task<PurchasingPolicyOptions> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var stored = await settings.FindSettingAsync(tenantId, SettingKey, cancellationToken);

        if (stored is null)
            return defaultPolicy;

        try
        {
            return JsonSerializer.Deserialize<PurchasingPolicyOptions>(stored) ?? defaultPolicy;
        }
        catch (JsonException)
        {
            return defaultPolicy;
        }
    }
}
