using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A capability. The unit the enforcement point actually checks.
/// </summary>
/// <remarks>
/// Roles are an administrative grouping convenience and are NEVER checked at the
/// enforcement point:
///
///   if (user.Role == "Manager")            // wrong — the day a customer wants a
///                                          // Supervisor who can refund but not
///                                          // void, you are editing enforcement code
///   RequirePermission("sales.refund.approve")   // right — capability, not identity
///
/// Naming: module.resource.action[.qualifier]. Consistency matters more than the
/// specific scheme. See ADR 013.
/// </remarks>
public sealed class Permission : Entity<Guid>
{
    private Permission() { }

    public static Permission Define(string code, string module, string description) => new()
    {
        Id = SequentialId.New(),
        Code = code,
        Module = module,
        Description = description
    };

    /// <summary>e.g. "sales.refund.approve". Stable — clients and roles reference it.</summary>
    public string Code { get; private set; } = null!;

    public string Module { get; private set; } = null!;
    public string Description { get; private set; } = null!;
}

/// <summary>The level at which a role assignment grants its permissions.</summary>
/// <remarks>
/// Note there is no Tenant scope. Tenant is a SECURITY boundary and is never
/// granted — it is established by authentication. These are all AUTHORIZATION
/// scopes within an already-established tenant. See ADR 006.
/// </remarks>
public enum ScopeType
{
    Company = 0,
    Branch = 1,
    Warehouse = 2
}

/// <summary>A named bundle of permissions.</summary>
public sealed class Role : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private readonly List<Guid> _permissionIds = [];

    private Role() { }

    public static Role Create(string name, string description, bool isSystemRole = false) => new()
    {
        Id = SequentialId.New(),
        Name = name,
        Description = description,
        IsSystemRole = isSystemRole
    };

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;

    /// <summary>Seeded roles (Cashier, Supervisor, Manager) that cannot be deleted.</summary>
    public bool IsSystemRole { get; private set; }

    public IReadOnlyList<Guid> PermissionIds => _permissionIds.AsReadOnly();

    public void Grant(Guid permissionId)
    {
        if (!_permissionIds.Contains(permissionId))
            _permissionIds.Add(permissionId);
    }

    public void Revoke(Guid permissionId) => _permissionIds.Remove(permissionId);

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

/// <summary>
/// Grants a role's permissions to a user AT A SPECIFIC SCOPE.
/// </summary>
/// <remarks>
/// "Manager at Branch 12", not "Manager". A regional manager holds the same role at
/// six branches; a group controller holds a reporting role at three companies.
/// Without the scope, you end up with per-branch role duplicates named
/// "Manager_Branch12" and a permissions system nobody can administer.
/// </remarks>
public sealed class RoleAssignment : Entity<Guid>
{
    private RoleAssignment() { }

    internal static RoleAssignment Create(Guid userId, Guid roleId, ScopeType scopeType, Guid scopeId) => new()
    {
        Id = SequentialId.New(),
        UserId = userId,
        RoleId = roleId,
        ScopeType = scopeType,
        ScopeId = scopeId
    };

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public ScopeType ScopeType { get; private set; }
    public Guid ScopeId { get; private set; }
}
