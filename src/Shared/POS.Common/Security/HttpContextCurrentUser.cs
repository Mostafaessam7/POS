using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace POS.Common.Security;

/// <summary>
/// Reads the current principal from the validated access token.
/// </summary>
/// <remarks>
/// Every value here comes from a SIGNED claim, never from a header or route value.
/// The distinction matters most for <see cref="TerminalId"/>: a caller able to name
/// its own terminal could attribute a sale to a till in another branch, which defeats
/// both the cash reconciliation and the sync scoping. See ADR 014.
///
/// Unauthenticated requests get an instance with every property null or empty rather
/// than a throwing one. Authorization is the pipeline's job; this type only reports
/// what the token said, and making it throw would turn every anonymous endpoint into
/// a 500.
/// </remarks>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => ReadGuid(ClaimTypes.NameIdentifier) ?? ReadGuid("sub");

    public Guid? TerminalId => ReadGuid(PosClaimTypes.Terminal);

    public Guid? BranchId => ReadGuid(PosClaimTypes.Branch);

    public IReadOnlySet<Guid> CompanyIds => ReadGuidSet(PosClaimTypes.Company);

    /// <remarks>
    /// A token carries the branch it was issued for, not the set the user may reach.
    /// The full set is a permission-scope question and is answered by the resolver,
    /// so this deliberately reports only what the token proves.
    /// </remarks>
    public IReadOnlySet<Guid> BranchIds => ReadGuidSet(PosClaimTypes.Branch);

    public int PermissionVersion =>
        int.TryParse(
            Principal?.FindFirstValue(PosClaimTypes.PermissionVersion),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var version)
            ? version
            : 0;

    private Guid? ReadGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirstValue(claimType), out var value) ? value : null;

    private HashSet<Guid> ReadGuidSet(string claimType)
    {
        var principal = Principal;

        if (principal is null)
            return [];

        return principal.FindAll(claimType)
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
    }
}

/// <summary>
/// The principal for work that has no user: migrations, background jobs, sync ingest.
/// </summary>
/// <remarks>
/// Registered instead of <see cref="HttpContextCurrentUser"/> wherever there is no
/// HTTP request. Audit rows written under it carry <see cref="Guid.Empty"/> as the
/// actor, which is intentionally distinguishable from a real user id when someone is
/// reading an audit trail six months later.
/// </remarks>
public sealed class SystemUser : ICurrentUser
{
    public static SystemUser Instance { get; } = new();

    public Guid? UserId => null;
    public Guid? TerminalId => null;
    public Guid? BranchId => null;
    public IReadOnlySet<Guid> CompanyIds { get; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> BranchIds { get; } = new HashSet<Guid>();
    public int PermissionVersion => 0;
}
