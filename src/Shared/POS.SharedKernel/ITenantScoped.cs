namespace POS.SharedKernel;

/// <summary>
/// Marks an entity as belonging to exactly one tenant.
/// </summary>
/// <remarks>
/// TENANT IS A SECURITY BOUNDARY. Crossing it is never legitimate, is enforced by
/// infrastructure (query filter + SaveChanges interceptor + generated test suite),
/// and no permission can grant access across it.
///
/// This is deliberately distinct from <see cref="IBranchScoped"/>. Branch, company,
/// and warehouse are AUTHORIZATION boundaries — crossing them is routine and is
/// governed by permissions, which are data. Conflating the two produces either a
/// security hole or a permission system nobody can reason about. See ADR 006.
///
/// The setter is not public: TenantId is assigned by the persistence interceptor
/// from the ambient context and can never be supplied by a caller.
/// </remarks>
public interface ITenantScoped
{
    public Guid TenantId { get; }
}

/// <summary>
/// Marks an entity as belonging to a branch. An AUTHORIZATION boundary — see
/// <see cref="ITenantScoped"/> for why the distinction matters.
/// </summary>
public interface IBranchScoped
{
    public Guid BranchId { get; }
}

/// <summary>Marks an entity as belonging to a legal entity within a tenant.</summary>
public interface ICompanyScoped
{
    public Guid CompanyId { get; }
}
