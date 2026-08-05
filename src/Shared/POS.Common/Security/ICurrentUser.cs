namespace POS.Common.Security;

/// <summary>The authenticated principal for the current unit of work.</summary>
/// <remarks>
/// A POS request has TWO principals, not one. The terminal is a long-lived,
/// physically-located device; the user is a short session on top of it. Separating
/// them is what allows a stolen till to be revoked without touching any user
/// account, and what scopes a terminal's data sync to a single branch.
/// A token carrying a user but no terminal cannot complete a sale. See ADR 014.
/// </remarks>
public interface ICurrentUser
{
    public Guid? UserId { get; }
    public Guid? TerminalId { get; }
    public Guid? BranchId { get; }

    /// <summary>Companies this user may act within. An authorization scope, not a security boundary.</summary>
    public IReadOnlySet<Guid> CompanyIds { get; }

    public IReadOnlySet<Guid> BranchIds { get; }

    /// <summary>Monotonic version used to invalidate the cached permission set. See ADR 013.</summary>
    public int PermissionVersion { get; }
}
