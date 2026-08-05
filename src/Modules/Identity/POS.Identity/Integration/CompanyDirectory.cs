using Microsoft.EntityFrameworkCore;
using POS.Contracts.Identity;
using POS.Identity.Persistence;

namespace POS.Identity.Integration;

/// <inheritdoc cref="ICompanyDirectory"/>
public sealed class CompanyDirectory(IdentityDbContext db) : ICompanyDirectory
{
    public Task<CompanyFiscalIdentity?> FindFiscalIdentityAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        db.Companies
          .AsNoTracking()
          .Where(c => c.Id == companyId)
          .Select(c => new CompanyFiscalIdentity(
              c.FiscalProfileCode,
              c.CountryCode,
              c.TaxRegistrationNumber,
              c.BaseCurrency))
          .FirstOrDefaultAsync(cancellationToken)!;
}
