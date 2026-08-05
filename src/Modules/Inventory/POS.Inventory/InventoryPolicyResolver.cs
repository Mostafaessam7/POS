using System.Text.Json;
using POS.Contracts.Identity;

namespace POS.Inventory;

/// <summary>
/// Resolves the EFFECTIVE inventory policy for a tenant: its own stored override if
/// it has set one, otherwise this deployment's default.
/// </summary>
/// <remarks>
/// Mirrors <c>PurchasingPolicyResolver</c> exactly — see its remarks for the full
/// rationale. <see cref="InventoryPolicyOptions"/>'s own remarks name this resolver as
/// the contained follow-up once a tenant settings store existed; the one caller
/// (<c>StockTransferService.WriteOffVarianceAsync</c>) already goes through
/// <c>InventoryPolicyOptions</c>, so this changes what it resolves, not how it's used.
/// </remarks>
public sealed class InventoryPolicyResolver(InventoryPolicyOptions defaultPolicy, ITenantSettingsDirectory settings)
{
    public const string SettingKey = "inventory.policy";

    public async Task<InventoryPolicyOptions> ResolveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var stored = await settings.FindSettingAsync(tenantId, SettingKey, cancellationToken);

        if (stored is null)
            return defaultPolicy;

        try
        {
            return JsonSerializer.Deserialize<InventoryPolicyOptions>(stored) ?? defaultPolicy;
        }
        catch (JsonException)
        {
            return defaultPolicy;
        }
    }
}
