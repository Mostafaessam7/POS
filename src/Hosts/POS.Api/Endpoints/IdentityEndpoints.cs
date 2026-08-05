using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using POS.Common.Errors;
using POS.Common.Validation;
using POS.Identity.Authentication;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// Password-based login and refresh-token rotation over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <c>TokenService</c> and <c>RefreshTokenService</c> have existed since the identity
/// milestone; nothing previously mapped either to an HTTP route — every integration
/// test mints a token in-process via <c>TokenService.Issue</c> directly. This is the
/// first real front door for a human (as opposed to a terminal, which never logs in
/// with a password — see ADR 015: a PIN is a separate, branch-scoped credential that
/// can never authenticate here).
/// </para>
/// <para>
/// <b>A login needs a tenant to look the user up in, and email alone cannot supply
/// one</b> — email is unique PER TENANT, not globally (the same person may legitimately
/// work for two merchants on the platform), so two different tenants can have a user
/// sharing an email address. The login request therefore carries the tenant's
/// <c>Subdomain</c> (already globally unique — see `Tenant`) alongside credentials,
/// the same "which workspace" step most multi-tenant products ask for. The lookup then
/// runs `IgnoreQueryFilters()` scoped explicitly to that tenant id, mirroring
/// `RefreshTokenService.RotateAsync`'s own reasoning: this happens before any tenant
/// context is established for the request, so the ambient filter has nothing to key
/// off yet.
/// </para>
/// <para>
/// Every failure path — unknown subdomain, unknown email, wrong password, locked
/// account — returns the SAME <c>auth.invalid_credentials</c> shape wherever a
/// distinction would let a caller enumerate which part was wrong. The one exception is
/// the lockout itself, which is intentionally a different, visible message: knowing
/// "the account is locked" (as opposed to "you may have got the password wrong")
/// doesn't tell an attacker anything they don't already know from having tried it
/// enough times to trigger the lockout.
/// </para>
/// <para>
/// <b>The refresh token never appears in a JSON body.</b> It is set as an
/// <c>HttpOnly</c> cookie, scoped to <c>/api/v1/auth</c>, so client-side script can
/// never read it — the whole point, since the alternative (a 14-day credential sitting
/// in <c>localStorage</c>) is a standing XSS-exfiltration target. <c>Secure</c> and
/// <c>SameSite=None</c> are set unconditionally: a real deployment has the SPA and API
/// on different domains, and modern browsers treat <c>http://localhost</c> as a secure
/// context, so this does not require HTTPS to exercise locally.
/// </para>
/// </remarks>
public static class IdentityEndpoints
{
    private const string RefreshCookieName = "pos_refresh_token";
    private const string AuthCookiePath = "/api/v1/auth";

    private static readonly Error InvalidCredentials = Error.Forbidden(
        "auth.invalid_credentials", "Invalid workspace, email, or password.");

    private static readonly Error AccountLocked = Error.Forbidden(
        "auth.account_locked",
        "This account is temporarily locked after repeated failed sign-in attempts. Try again later.");

    private static readonly Error RefreshMissing = Error.Forbidden(
        "auth.refresh_missing", "No refresh session was presented.");

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth").AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext httpContext,
            IdentityDbContext db,
            IPasswordHasher hasher,
            TokenService tokens,
            IClock clock,
            CancellationToken ct) =>
        {
            var normalizedSubdomain = request.Subdomain.Trim().ToLowerInvariant();

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Subdomain == normalizedSubdomain, ct);

            if (tenant is null)
                return InvalidCredentials.ToHttpResult();

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // IgnoreQueryFilters + an explicit TenantId match: there is no ambient
            // tenant for this request yet, so the automatic filter has nothing to
            // key off (it would filter to Guid.Empty and find nothing at all).
            var user = await db.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == normalizedEmail, ct);

            if (user is null || user.Status != UserStatus.Active)
                return InvalidCredentials.ToHttpResult();

            var now = clock.UtcNow;

            if (user.IsLockedOut(now))
                return AccountLocked.ToHttpResult();

            var verification = hasher.Verify(request.Password, user.PasswordHash, user.PasswordAlgorithm);

            if (!verification.IsValid)
            {
                user.RecordFailedLogin(now);
                await db.SaveChangesAsync(ct);
                return InvalidCredentials.ToHttpResult();
            }

            // Transparent cost-factor upgrade: a hash produced under a weaker policy
            // is replaced with one under the current policy on the very login that
            // proves the password is correct, so raising Argon2id's parameters later
            // never requires a forced password reset. See IPasswordHasher's remarks.
            if (verification.NeedsUpgrade)
            {
                var rehashed = hasher.Hash(request.Password);
                user.UpgradePasswordHash(rehashed.Hash, rehashed.Algorithm);
            }

            user.RecordSuccessfulLogin();

            var pair = tokens.Issue(user, tenant.Id, terminalId: null, branchId: null, companyIds: []);
            var refreshExpiresAt = now.AddDays(14);

            // A fresh family per login, exactly like a fresh terminal enrolment would
            // get one — this session's refresh chain is independent of any other
            // device or browser this user is signed into.
            db.RefreshTokens.Add(RefreshToken.Issue(
                user.Id,
                tenant.Id,
                TokenService.HashRefreshToken(pair.RefreshToken),
                familyId: Guid.CreateVersion7(),
                terminalId: null,
                deviceFingerprint: request.DeviceFingerprint ?? "web",
                now,
                TimeSpan.FromDays(14)));

            await db.SaveChangesAsync(ct);

            SetRefreshTokenCookie(httpContext, pair.RefreshToken, refreshExpiresAt);

            return Results.Ok(new LoginResponse(
                pair.AccessToken, pair.AccessTokenExpiresAt,
                tenant.Id, user.Id, user.DisplayName, user.Email));
        })
        .AddValidation<LoginRequest>();

        group.MapPost("/refresh", async (
            HttpContext httpContext,
            RefreshTokenService refresh,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!httpContext.Request.Cookies.TryGetValue(RefreshCookieName, out var presentedToken)
                || string.IsNullOrEmpty(presentedToken))
            {
                return RefreshMissing.ToHttpResult();
            }

            var result = await refresh.RotateAsync(presentedToken, ct);

            return result.Match(
                pair =>
                {
                    SetRefreshTokenCookie(httpContext, pair.RefreshToken, clock.UtcNow.AddDays(14));
                    return Results.Ok(new TokenResponse(pair.AccessToken, pair.AccessTokenExpiresAt));
                },
                error => error.ToHttpResult());
        });

        // Idempotent and never errors — a caller's intent to end their session must
        // succeed even if the cookie is already gone, matching the stance
        // RefreshTokenService.RevokeAsync itself takes.
        group.MapPost("/logout", async (
            HttpContext httpContext,
            RefreshTokenService refresh,
            CancellationToken ct) =>
        {
            if (httpContext.Request.Cookies.TryGetValue(RefreshCookieName, out var presentedToken)
                && !string.IsNullOrEmpty(presentedToken))
            {
                await refresh.RevokeAsync(presentedToken, ct);
            }

            ClearRefreshTokenCookie(httpContext);
            return Results.NoContent();
        });

        return app;
    }

    private static void SetRefreshTokenCookie(HttpContext httpContext, string token, DateTimeOffset expiresAt) =>
        httpContext.Response.Cookies.Append(RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Path = AuthCookiePath,
            Expires = expiresAt
        });

    private static void ClearRefreshTokenCookie(HttpContext httpContext) =>
        httpContext.Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = AuthCookiePath });
}

public sealed record LoginRequest(string Subdomain, string Email, string Password, string? DeviceFingerprint = null);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Subdomain).NotEmpty();
        RuleFor(r => r.Email).NotEmpty().EmailAddress();
        RuleFor(r => r.Password).NotEmpty();
    }
}

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid TenantId,
    Guid UserId,
    string DisplayName,
    string Email);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
