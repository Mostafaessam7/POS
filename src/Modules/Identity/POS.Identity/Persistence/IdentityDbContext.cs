using Microsoft.EntityFrameworkCore;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Identity.Domain;

namespace POS.Identity.Persistence;

/// <summary>
/// The Identity module's persistence boundary.
/// </summary>
/// <remarks>
/// One DbContext per module, each with its OWN migration history table
/// (see <see cref="MigrationsHistoryTable"/>). This is what lets Catalog ship a
/// migration without blocking an Identity deployment, and it is the seam along
/// which a module could later be extracted to its own database. See ADR 002.
///
/// There is no repository layer. DbContext IS the unit of work and DbSet IS the
/// repository; wrapping them adds indirection, loses IQueryable composition, and
/// forces every query shape to become a named method. See ADR 009.
/// </remarks>
public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options,
    ITenantContext tenantContext) : PosDbContext(options, tenantContext)
{
    public const string Schema = "identity";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Identity";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthorizationGrant> AuthorizationGrants => Set<AuthorizationGrant>();
    public DbSet<ProvisioningOperator> ProvisioningOperators => Set<ProvisioningOperator>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);

        // MUST be last. Applying the combined tenant + soft-delete filter centrally
        // means a new entity is filtered because it implements the marker interface,
        // not because somebody remembered to add a filter. Forgetting is the failure
        // mode, so the default is made safe. See ADR 006.
        TenantQueryFilter.ApplyTo(modelBuilder, this);

        base.OnModelCreating(modelBuilder);
    }
}
