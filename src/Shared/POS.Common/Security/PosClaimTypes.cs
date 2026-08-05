namespace POS.Common.Security;

/// <summary>
/// The claim types this platform issues and reads.
/// </summary>
/// <remarks>
/// These live in POS.Common rather than in the Identity module because both ends of
/// the contract need them and the dependency only runs one way: Identity ISSUES the
/// token and references Common; Common's tenant middleware and current-user adapter
/// READ it and cannot reference Identity. A duplicated string literal on the two
/// sides of that boundary is a silent authentication failure waiting for the first
/// typo, so there is exactly one definition and Identity forwards to it.
/// </remarks>
public static class PosClaimTypes
{
    public const string Tenant = "tenant_id";
    public const string Terminal = "terminal_id";
    public const string Branch = "branch_id";
    public const string Company = "company_id";
    public const string PermissionVersion = "perm_version";
}
