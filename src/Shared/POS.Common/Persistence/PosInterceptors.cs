using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Auditing;
using POS.Common.Tenancy;

namespace POS.Common.Persistence;

/// <summary>
/// Attaches the two interceptors that must run on every module context.
/// </summary>
/// <remarks>
/// These are not optional extras and are applied centrally for the same reason the
/// query filter is: a module that forgets one does not fail, it silently loses a
/// guarantee. <see cref="TenantGuardInterceptor"/> is the write-side half of tenant
/// isolation — query filters protect reads only — and
/// <see cref="AuditingInterceptor"/> is what makes "who changed this row" answerable
/// at all.
///
/// Order is deliberate. Auditing runs first so that a soft delete has already been
/// rewritten from Deleted to Modified by the time the tenant guard inspects entry
/// states; otherwise a cross-tenant soft delete would be classified as a delete of a
/// row the caller may not touch and would produce a confusing error instead of the
/// correct one.
/// </remarks>
public static class PosInterceptors
{
    public static DbContextOptionsBuilder AddPosInterceptors(
        this DbContextOptionsBuilder options,
        IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);

        return options.AddInterceptors(
            provider.GetRequiredService<AuditingInterceptor>(),
            provider.GetRequiredService<TenantGuardInterceptor>());
    }
}
