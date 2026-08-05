using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Contracts.Identity;
using POS.Identity.Authentication;
using POS.Identity.Authorization;
using POS.Identity.Integration;
using POS.Identity.Persistence;

namespace POS.Identity;

/// <summary>Identity's composition: persistence, token issuance, and authentication.</summary>
public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        string connectionString,
        TokenOptions tokenOptions,
        string? redisConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(tokenOptions);

        services.AddDbContext<IdentityDbContext>((provider, options) =>
            options.UsePosSqlServer<IdentityDbContext>(
                connectionString,
                IdentityDbContext.MigrationsHistoryTable,
                IdentityDbContext.Schema)
                .AddPosInterceptors(provider));

        services.AddSingleton(tokenOptions);
        services.AddScoped<TokenService>();
        services.AddScoped<RefreshTokenService>();

        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

        services.AddMemoryCache();

        // L2 for CachedPermissionResolver (ADR 013). Genuinely shared across every
        // instance when Redis is configured — closing the scale gap ADR 013 flagged
        // and left for later. Absent a connection string, AddDistributedMemoryCache
        // registers the SAME IDistributedCache abstraction backed by an in-process
        // dictionary, so the resolver's code path never branches on which backend is
        // live: single-instance dev and test environments get correct (if
        // non-shared) behaviour without a hard Redis dependency, and production gets
        // the real thing by supplying a connection string.
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "pos:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddScoped<IPermissionResolver, CachedPermissionResolver>();

        // Scoped, not singleton: the handler depends on the scoped resolver, which
        // depends on the scoped DbContext. Registering it as a singleton is the
        // classic captive-dependency bug and would keep one DbContext alive for the
        // lifetime of the process.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IPermissionScopeGuard, PermissionScopeGuard>();

        // Organisational facts other modules need, published through the contract.
        services.AddScoped<ICompanyDirectory, CompanyDirectory>();
        services.AddScoped<ITenantSettingsDirectory, TenantSettingsDirectory>();

        return services;
    }

    /// <summary>
    /// Wires JWT bearer validation using the same options the tokens are issued with.
    /// </summary>
    /// <remarks>
    /// Issuer, audience and lifetime are ALL validated. Turning any of them off is the
    /// usual way a bearer setup becomes "any token signed by anything we trust", and
    /// the signing key here is shared across the estate.
    ///
    /// <c>ClockSkew</c> is cut from the five-minute default to thirty seconds. Access
    /// tokens live twelve minutes precisely so that a stolen one expires quickly
    /// (ADR 013); a five-minute grace period would give back nearly half of that.
    /// </remarks>
    public static IServiceCollection AddPosAuthentication(
        this IServiceCollection services,
        TokenOptions tokenOptions)
    {
        ArgumentNullException.ThrowIfNull(tokenOptions);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = tokenOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = tokenOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(tokenOptions.SigningKey),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();

        // Replaces the default provider so `.RequirePermission("x.y.z")` resolves
        // without every permission being registered as a named policy by hand.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        return services;
    }

    /// <summary>Reads the signing key from configuration, refusing anything too weak to matter.</summary>
    /// <remarks>
    /// HMAC-SHA256 keys shorter than the 256-bit hash output reduce the security of
    /// the construction, and <c>IdentityModel</c> throws on them at first use — at
    /// request time, in production. Failing here instead turns a runtime outage into a
    /// startup error with a sentence explaining the fix.
    /// </remarks>
    public static byte[] ReadSigningKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Identity:SigningKey is not configured. Set it in configuration or in the " +
                "POS_Identity__SigningKey environment variable. It must be at least 32 characters.");
        }

        var key = Encoding.UTF8.GetBytes(configured);

        return key.Length >= 32
            ? key
            : throw new InvalidOperationException(
                $"Identity:SigningKey is {key.Length} bytes. HMAC-SHA256 requires at least 32.");
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class IdentityDbContextFactory : PosDesignTimeDbContextFactory<IdentityDbContext>
{
    protected override string MigrationsHistoryTable => IdentityDbContext.MigrationsHistoryTable;

    protected override string Schema => IdentityDbContext.Schema;

    protected override IdentityDbContext Create(
        DbContextOptions<IdentityDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
