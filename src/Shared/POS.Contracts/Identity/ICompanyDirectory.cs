namespace POS.Contracts.Identity;

/// <summary>
/// Read-only access to the organisational facts other modules legitimately need.
/// </summary>
/// <remarks>
/// A NARROW WINDOW, deliberately. Company, branch and terminal live in Identity, and
/// several modules need a few facts about them — a fiscal document has to carry the
/// seller's tax registration, and which fiscal regime applies is a property of the
/// company. Exposing the <c>Company</c> aggregate through a contract would make every
/// consumer a client of Identity's model; exposing three fields does not.
///
/// Everything here is a fact ABOUT an organisation, never a way to change one.
/// </remarks>
public interface ICompanyDirectory
{
    /// <summary>The company's fiscal identity, or null if no such company exists.</summary>
    public Task<CompanyFiscalIdentity?> FindFiscalIdentityAsync(
        Guid companyId,
        CancellationToken cancellationToken = default);
}

/// <param name="FiscalProfileCode">
/// Which jurisdiction plugin governs this company's documents. Company data rather than
/// deployment configuration, because one platform instance serves merchants trading in
/// different countries.
/// </param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, as the profile expects it.</param>
/// <param name="TaxRegistrationNumber">Printed on every document the company issues.</param>
/// <param name="BaseCurrency">ISO 4217.</param>
public sealed record CompanyFiscalIdentity(
    string FiscalProfileCode,
    string CountryCode,
    string TaxRegistrationNumber,
    string BaseCurrency);
