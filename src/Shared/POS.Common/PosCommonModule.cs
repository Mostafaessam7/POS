using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Common.Auditing;
using POS.Common.Errors;
using POS.Common.Security;
using POS.Common.Tenancy;
using POS.SharedKernel;

namespace POS.Common;

/// <summary>
/// The cross-cutting services every module context depends on.
/// </summary>
/// <remarks>
/// Registered once by the host, before any module. Every lifetime here is a
/// deliberate choice:
///
/// <see cref="ITenantContext"/> is SCOPED, and must be. It is mutable state resolved
/// per request; a singleton would let one request's tenant leak into another's, which
/// is the exact breach the whole tenancy design exists to prevent. It is registered
/// once as <see cref="TenantContext"/> and aliased to the interface, so both
/// resolutions return the SAME instance — the middleware writes through the concrete
/// type and every context reads through the interface.
///
/// The interceptors are SCOPED because they capture the scoped tenant and user.
///
/// <see cref="IClock"/> is a singleton because it is stateless, and it exists at all
/// so that no code anywhere reads the system clock directly — architecture rule 8
/// fails the build on <c>DateTime.Now</c>.
/// </remarks>
public static class PosCommonModule
{
    public static IServiceCollection AddPosCommon(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.TryAddSingleton<IClock, SystemClock>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.TryAddScoped<ICurrentUser, HttpContextCurrentUser>();

        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<TenantGuardInterceptor>();

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }
}
