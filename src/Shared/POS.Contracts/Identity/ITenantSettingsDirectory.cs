namespace POS.Contracts.Identity;

/// <summary>
/// Read-only access to a tenant's stored configuration overrides.
/// </summary>
/// <remarks>
/// The write side lives behind ordinary tenant-scoped, permission-gated endpoints in
/// the host (see <c>SettingsEndpoints</c>) — nothing outside Identity needs to WRITE a
/// setting, only to read whichever override the current tenant has chosen, if any.
///
/// The value is an opaque JSON blob keyed by a caller-chosen string. Identity does not
/// know or care what shape "purchasing.policy" or "inventory.policy" holds, the same
/// way it does not know what a fiscal profile code means (<see cref="ICompanyDirectory"/>).
/// Interpreting the JSON is entirely the consuming module's job — see
/// <c>PurchasingPolicyResolver</c>/<c>InventoryPolicyResolver</c> for the reference
/// shape: read the override if one exists, fall back to the deployment-wide default
/// otherwise.
/// </remarks>
public interface ITenantSettingsDirectory
{
    /// <summary>The raw JSON stored under <paramref name="key"/> for this tenant, or null if never set.</summary>
    public Task<string?> FindSettingAsync(Guid tenantId, string key, CancellationToken cancellationToken = default);
}
