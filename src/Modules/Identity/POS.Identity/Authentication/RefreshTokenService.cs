using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.SharedKernel;

namespace POS.Identity.Authentication;

public sealed class RefreshTokenService(
    IdentityDbContext db,
    TokenService tokens,
    IClock clock,
    ILogger<RefreshTokenService> logger)
{
    public static readonly Error Invalid = Error.Forbidden("auth.refresh_invalid", "The refresh token is not valid.");
    public static readonly Error Reused = Error.Forbidden("auth.refresh_reused", "The session has been terminated for security reasons.");

    /// <summary>
    /// Rotates a refresh token: consumes the presented one, issues a replacement in
    /// the same family, and revokes the whole family if the presented token was
    /// already used.
    /// </summary>
    public async Task<Result<TokenPair>> RotateAsync(string presentedToken, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hash = TokenService.HashRefreshToken(presentedToken);

        // Refresh happens BEFORE a tenant is established for this request, so the
        // tenant filter must be bypassed. This is one of exactly three legitimate
        // callers of the bypass, and it is logged.
        var existing = await db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null)
            return Result<TokenPair>.Failure(Invalid);

        if (existing.Status == RefreshTokenStatus.Used)
        {
            // A consumed token has been presented again. Either the legitimate client
            // retried after a lost response, or the token was stolen and replayed. We
            // cannot distinguish the two, so we assume the worse case. The cost of
            // being wrong is one re-login; the cost of the alternative is a silent
            // persistent backdoor.
            var family = await db.RefreshTokens
                .IgnoreQueryFilters()
                .Where(t => t.FamilyId == existing.FamilyId && t.Status == RefreshTokenStatus.Active)
                .ToListAsync(ct);

            foreach (var token in family) token.Revoke();
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Refresh token reuse detected. Family {FamilyId} revoked ({Count} active tokens) for user {UserId}.",
                existing.FamilyId, family.Count, existing.UserId);

            return Result<TokenPair>.Failure(Reused);
        }

        if (!existing.IsUsable(now))
            return Result<TokenPair>.Failure(Invalid);

        var user = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);

        if (user is null || user.Status != UserStatus.Active)
            return Result<TokenPair>.Failure(Invalid);

        var pair = tokens.Issue(user, existing.TenantId, existing.TerminalId, branchId: null, companyIds: []);

        var replacement = RefreshToken.Issue(
            user.Id,
            existing.TenantId,
            TokenService.HashRefreshToken(pair.RefreshToken),
            existing.FamilyId,
            existing.TerminalId,
            existing.DeviceFingerprint,
            now,
            TimeSpan.FromDays(14));

        existing.Consume(replacement.Id, now);
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(ct);

        return Result<TokenPair>.Success(pair);
    }

    /// <summary>
    /// Revokes the token presented at logout, if it still exists and is active.
    /// </summary>
    /// <remarks>
    /// Deliberately a no-op — never an error — when the token is unknown or already
    /// inactive: logout must succeed from the caller's point of view even if the
    /// session was already gone (a double-click, a token that already expired), the
    /// same idempotent stance <c>ProvisioningOperator.Revoke</c> takes.
    /// </remarks>
    public async Task RevokeAsync(string presentedToken, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(presentedToken);

        var existing = await db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null || existing.Status != RefreshTokenStatus.Active)
            return;

        existing.Revoke();
        await db.SaveChangesAsync(ct);
    }
}
