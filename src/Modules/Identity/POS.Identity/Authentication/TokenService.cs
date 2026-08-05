using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using POS.Common.Security;
using POS.Identity.Domain;
using POS.SharedKernel;

namespace POS.Identity.Authentication;

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);

public sealed record TokenOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required byte[] SigningKey { get; init; }

    /// <summary>
    /// Deliberately short. The access token is not revocable, so its lifetime IS the
    /// blast radius of a stolen token. Everything expensive to revoke lives behind
    /// the perm_version lookup instead. See ADR 013.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(12);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(14);
}

/// <summary>
/// Issues access tokens and manages refresh token rotation with family reuse detection.
/// </summary>
/// <remarks>
/// The token carries TWO principals — user and terminal — plus the authorization
/// SCOPES the user may act within (companies, branches) and a permission VERSION.
/// It deliberately does NOT carry the permission set itself: with 200+ permissions
/// across several scopes that bloats every request, and worse, it makes revocation
/// wait for expiry. A manager stripping a cashier's refund rights must take effect
/// now, not in twelve minutes. See ADR 013.
///
/// The refresh token is opaque random bytes, never a JWT. It is stored hashed, is
/// single-use, and belongs to a FAMILY. Presenting an already-consumed token means
/// either a race or a theft; we cannot tell which, so we assume theft and revoke
/// the entire family. That converts a stolen refresh token from a persistent
/// backdoor into a detectable, self-limiting incident. See ADR 005.
/// </remarks>
public sealed class TokenService(TokenOptions options, IClock clock)
{
    // Forwarded, not redeclared. The reading side lives in POS.Common (which cannot
    // reference Identity), so a second literal here would be a silent authentication
    // failure the first time one of the two was edited.
    public const string TenantClaim = PosClaimTypes.Tenant;
    public const string TerminalClaim = PosClaimTypes.Terminal;
    public const string BranchClaim = PosClaimTypes.Branch;
    public const string CompanyClaim = PosClaimTypes.Company;
    public const string PermissionVersionClaim = PosClaimTypes.PermissionVersion;

    public TokenPair Issue(User user, Guid tenantId, Guid? terminalId, Guid? branchId, IEnumerable<Guid> companyIds)
    {
        var now = clock.UtcNow;
        var expiresAt = now.Add(options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, SequentialId.New().ToString()),
            new(TenantClaim, tenantId.ToString()),
            new(PermissionVersionClaim, user.PermissionVersion.ToString(CultureInfo.InvariantCulture)),
        };

        if (terminalId is { } t) claims.Add(new Claim(TerminalClaim, t.ToString()));
        if (branchId is { } br) claims.Add(new Claim(BranchClaim, br.ToString()));
        claims.AddRange(companyIds.Select(c => new Claim(CompanyClaim, c.ToString())));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(options.SigningKey),
            SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new TokenPair(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            GenerateRefreshToken(),
            expiresAt);
    }

    /// <summary>256 bits of CSPRNG output. Not a JWT — it carries no claims and is a pure bearer secret.</summary>
    public static string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256, NOT Argon2id. This is deliberate and is the opposite of the password
    /// decision. A refresh token is 256 bits of full-entropy random data, so it is
    /// not brute-forceable and needs no work factor; and it is verified on a hot path
    /// where a 19 MiB memory-hard hash per request would be a self-inflicted DoS.
    /// Password hashing needs Argon2id precisely because passwords are LOW entropy.
    /// </summary>
    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
