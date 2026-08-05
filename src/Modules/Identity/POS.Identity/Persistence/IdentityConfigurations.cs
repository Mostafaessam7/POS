using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Identity.Domain;

namespace POS.Identity.Persistence;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Subdomain).HasMaxLength(63).IsRequired();

        // Tenant is the ONE table that is not itself tenant-scoped, so this unique
        // index is global and unfiltered by design.
        builder.HasIndex(e => e.Subdomain).IsUnique();
    }
}

public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("Companies");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.TaxRegistrationNumber).HasMaxLength(50).IsRequired();

        // ISO 4217. Fixed length because it is, and because char(3) indexes better.
        builder.Property(e => e.BaseCurrency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasIndex(e => e.TenantId);
    }
}

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.TimeZoneId).HasMaxLength(100).IsRequired();

        // FILTERED. Without WHERE IsDeleted = 0 a branch code can never be reused
        // after deletion, and merchants reuse codes constantly. Every unique index
        // on a soft-deletable entity in this solution carries this filter, and
        // SoftDeleteIndexTests asserts it over the whole model.
        builder.HasIndex(e => new { e.TenantId, e.CompanyId, e.Code })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");
    }
}

public sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouses");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Kind).HasConversion<int>();

        builder.HasIndex(e => new { e.TenantId, e.BranchId, e.Code })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");
    }
}

public sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> builder)
    {
        builder.ToTable("Terminals");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CertificateThumbprint).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>();

        // Terminal is NOT soft-deletable: a revoked till must remain resolvable
        // because historical sales reference it. Status handles retirement.
        builder.HasIndex(e => new { e.TenantId, e.BranchId, e.Code }).IsUnique();
        builder.HasIndex(e => e.CertificateThumbprint).IsUnique();
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Email).HasMaxLength(256).IsRequired();
        builder.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();

        // Argon2id encoded hashes are ~100 chars; 512 leaves room for parameter
        // growth without a migration when cost factors are raised. See ADR 012.
        builder.Property(e => e.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(e => e.PasswordAlgorithm).HasMaxLength(50).IsRequired();
        builder.Property(e => e.PinHash).HasMaxLength(512);
        builder.Property(e => e.Status).HasConversion<int>();

        // Email is unique PER TENANT, not globally. The same person may legitimately
        // work for two merchants on the platform.
        builder.HasIndex(e => new { e.TenantId, e.Email })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");

        builder.OwnsMany(e => e.RoleAssignments, a =>
        {
            a.ToTable("UserRoleAssignments");
            a.WithOwner().HasForeignKey("UserId");
            a.HasKey(x => x.Id);
            a.Property(x => x.ScopeType).HasConversion<int>();
            a.HasIndex(x => new { x.RoleId, x.ScopeType, x.ScopeId });
        });
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(500).IsRequired();

        builder.PrimitiveCollection(e => e.PermissionIds).HasColumnName("PermissionIds");

        builder.HasIndex(e => new { e.TenantId, e.Name })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0");
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Module).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(500).IsRequired();

        // Permissions are platform reference data, not tenant data. The catalogue
        // is defined in code (Permissions.cs) and seeded; tenants compose them into
        // roles but cannot invent new ones.
        builder.HasIndex(e => e.Code).IsUnique();
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(e => e.Id);

        // Hashed at rest. A refresh token is a credential; a database leak must not
        // hand the attacker working sessions. See ADR 005.
        builder.Property(e => e.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(e => e.DeviceFingerprint).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>();

        builder.HasIndex(e => e.TokenHash).IsUnique();

        // The reuse-detection query: given a presented token, revoke its whole family.
        builder.HasIndex(e => e.FamilyId);

        // Supports the expiry sweep without scanning.
        builder.HasIndex(e => new { e.Status, e.ExpiresAt });
    }
}

public sealed class TenantSettingConfiguration : IEntityTypeConfiguration<TenantSetting>
{
    public void Configure(EntityTypeBuilder<TenantSetting> builder)
    {
        builder.ToTable("TenantSettings");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Key).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Value).IsRequired();

        // "Setting" is an upsert, not an append — a tenant has at most one override
        // per key at any time.
        builder.HasIndex(e => new { e.TenantId, e.Key }).IsUnique();
    }
}

public sealed class ProvisioningOperatorConfiguration : IEntityTypeConfiguration<ProvisioningOperator>
{
    public void Configure(EntityTypeBuilder<ProvisioningOperator> builder)
    {
        builder.ToTable("ProvisioningOperators");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();

        // Hashed at rest, same stance as RefreshToken.TokenHash — this is a
        // credential, not a label.
        builder.Property(e => e.KeyHash).HasMaxLength(128).IsRequired();

        // Platform reference data, not tenant data — same as Permission and Tenant.
        // Global and unfiltered by design.
        builder.HasIndex(e => e.KeyHash).IsUnique();
        builder.HasIndex(e => e.Name).IsUnique();
    }
}

public sealed class AuthorizationGrantConfiguration : IEntityTypeConfiguration<AuthorizationGrant>
{
    public void Configure(EntityTypeBuilder<AuthorizationGrant> builder)
    {
        builder.ToTable("AuthorizationGrants");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.PermissionCode).HasMaxLength(100).IsRequired();
        builder.Property(e => e.CommandType).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CommandFingerprint).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500).IsRequired();

        // Override frequency per approving manager is a fraud indicator and needs to
        // be a cheap query from day one, not a table scan added after an incident.
        builder.HasIndex(e => new { e.TenantId, e.ApprovedByUserId, e.IssuedAt });
        builder.HasIndex(e => new { e.TenantId, e.ApprovedOffline, e.IssuedAt });
    }
}
