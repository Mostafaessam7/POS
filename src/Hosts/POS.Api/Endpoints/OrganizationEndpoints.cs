using Microsoft.EntityFrameworkCore;
using POS.Identity.Persistence;

namespace POS.Api.Endpoints;

/// <summary>
/// Read-only organisational structure — companies, their branches, and each branch's
/// warehouses — for clients that need to populate a picker rather than ask an
/// operator to paste a GUID.
/// </summary>
/// <remarks>
/// Every raising-a-document endpoint (a purchase order, a stock adjustment) takes a
/// company/branch/warehouse id as a required field, correctly — the domain should
/// never guess which one an ambiguous request meant. Something still has to tell a
/// caller what those ids ARE, though, and until this existed the only way to find
/// out was to read the response from tenant provisioning (a one-time, ops-only call)
/// or query the database directly. This is the tenant-scoped, ordinary-permission
/// equivalent: any authenticated user in the tenant can see the SHAPE of their own
/// organisation, the same way they could read it off a printed store list.
/// </remarks>
public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/organization", async (IdentityDbContext db, CancellationToken ct) =>
        {
            var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
            var branches = await db.Branches.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
            var warehouses = await db.Warehouses.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);

            var response = companies.Select(company => new CompanyResponse(
                company.Id,
                company.Name,
                branches
                    .Where(b => b.CompanyId == company.Id)
                    .Select(branch => new BranchResponse(
                        branch.Id,
                        branch.Name,
                        warehouses
                            .Where(w => w.BranchId == branch.Id)
                            .Select(w => new WarehouseResponse(w.Id, w.Name, w.Code))
                            .ToList()))
                    .ToList()));

            return Results.Ok(new OrganizationResponse(response.ToList()));
        })
        .RequireAuthorization();

        return app;
    }
}

public sealed record OrganizationResponse(IReadOnlyList<CompanyResponse> Companies);

public sealed record CompanyResponse(Guid Id, string Name, IReadOnlyList<BranchResponse> Branches);

public sealed record BranchResponse(Guid Id, string Name, IReadOnlyList<WarehouseResponse> Warehouses);

public sealed record WarehouseResponse(Guid Id, string Name, string Code);
