using Microsoft.EntityFrameworkCore;
using POS.Contracts.Identity;
using POS.Identity.Persistence;

namespace POS.Identity.Integration;

/// <inheritdoc cref="ITenantSettingsDirectory"/>
public sealed class TenantSettingsDirectory(IdentityDbContext db) : ITenantSettingsDirectory
{
    public Task<string?> FindSettingAsync(Guid tenantId, string key, CancellationToken cancellationToken = default) =>
        db.TenantSettings
          .AsNoTracking()
          .Where(s => s.TenantId == tenantId && s.Key == key)
          .Select(s => s.Value)
          .FirstOrDefaultAsync(cancellationToken)!;
}
