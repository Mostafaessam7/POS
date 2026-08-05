using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>A person who can authenticate.</summary>
public sealed class User : AggregateRoot<Guid>, ITenantScoped, IAuditable, ISoftDeletable
{
    private readonly List<RoleAssignment> _roleAssignments = [];

    private User() { }

    public static User Create(string email, string displayName, string passwordHash, string passwordAlgorithm) => new()
    {
        Id = SequentialId.New(),
        Email = email.ToLowerInvariant(),
        DisplayName = displayName,
        PasswordHash = passwordHash,
        PasswordAlgorithm = passwordAlgorithm,
        Status = UserStatus.Active,
        PermissionVersion = 1
    };

    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;

    public string PasswordHash { get; private set; } = null!;

    /// <summary>
    /// Algorithm and parameters used to produce <see cref="PasswordHash"/>.
    /// </summary>
    /// <remarks>
    /// Stored so hashes can be upgraded transparently on successful login when
    /// parameters are strengthened. Without it, raising the cost factor requires a
    /// forced password reset for every user, or two code paths forever.
    /// </remarks>
    public string PasswordAlgorithm { get; private set; } = null!;

    /// <summary>
    /// Terminal PIN hash. A SEPARATE credential from the password — see ADR 015.
    /// A PIN is valid only on an enrolled terminal, is branch-scoped, and can never
    /// authenticate against the web back office.
    /// </summary>
    public string? PinHash { get; private set; }

    public UserStatus Status { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Bumped whenever effective permissions change. The access token carries this
    /// value; a mismatch against the cached set forces a reload, which is what makes
    /// revocation immediate rather than waiting for token expiry. See ADR 013.
    /// </summary>
    public int PermissionVersion { get; private set; }

    public IReadOnlyList<RoleAssignment> RoleAssignments => _roleAssignments.AsReadOnly();

    public void AssignRole(Guid roleId, ScopeType scopeType, Guid scopeId)
    {
        if (_roleAssignments.Any(a => a.RoleId == roleId && a.ScopeType == scopeType && a.ScopeId == scopeId))
            return;

        _roleAssignments.Add(RoleAssignment.Create(Id, roleId, scopeType, scopeId));
        PermissionVersion++;
    }

    public void RevokeRole(Guid roleId, ScopeType scopeType, Guid scopeId)
    {
        var removed = _roleAssignments.RemoveAll(a =>
            a.RoleId == roleId && a.ScopeType == scopeType && a.ScopeId == scopeId);

        if (removed > 0) PermissionVersion++;
    }

    public void SetPin(string pinHash)
    {
        PinHash = pinHash;
        PermissionVersion++;
    }

    /// <summary>
    /// Records a failed authentication and locks the account after a threshold.
    /// </summary>
    /// <remarks>
    /// Lockout is per user AND enforced per terminal by the rate limiter. A PIN has
    /// a small keyspace, so its safety depends entirely on these constraints plus
    /// the requirement that it only works on an enrolled device.
    /// </remarks>
    public void RecordFailedLogin(DateTimeOffset now, int threshold = 5, int lockoutMinutes = 15)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= threshold)
            LockedUntil = now.AddMinutes(lockoutMinutes);
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil.HasValue && LockedUntil > now;

    public void UpgradePasswordHash(string hash, string algorithm)
    {
        PasswordHash = hash;
        PasswordAlgorithm = algorithm;
    }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

public enum UserStatus { Active = 0, Disabled = 1 }
