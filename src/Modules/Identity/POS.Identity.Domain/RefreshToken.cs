using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// An opaque, single-use, rotating refresh credential.
/// </summary>
/// <remarks>
/// FAMILY-BASED REUSE DETECTION is the point of this design and is not optional.
///
/// Each refresh issues a new token and invalidates its predecessor. If an
/// already-used token is presented, the only two explanations are a stolen token
/// being replayed or a legitimate client racing itself — and we cannot distinguish
/// them. So we revoke the ENTIRE FAMILY and raise a security event.
///
/// This converts a stolen refresh token from a silent, persistent backdoor into a
/// detectable, self-limiting incident: the attacker's use locks out the victim,
/// who reports it.
///
/// The token value is HASHED AT REST. It is a credential; treat it like a password.
/// </remarks>
public sealed class RefreshToken : Entity<Guid>
{
    private RefreshToken() { }

    public static RefreshToken Issue(
        Guid userId,
        Guid tenantId,
        string tokenHash,
        Guid familyId,
        Guid? terminalId,
        string deviceFingerprint,
        DateTimeOffset now,
        TimeSpan lifetime) => new()
        {
            Id = SequentialId.New(),
            UserId = userId,
            TenantId = tenantId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            TerminalId = terminalId,
            DeviceFingerprint = deviceFingerprint,
            IssuedAt = now,
            ExpiresAt = now.Add(lifetime),
            Status = RefreshTokenStatus.Active
        };

    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>SHA-256 of the token value. The plaintext is never persisted.</summary>
    public string TokenHash { get; private set; } = null!;

    /// <summary>
    /// Shared by every token descended from one login. Revoking the family
    /// invalidates the whole chain at once.
    /// </summary>
    public Guid FamilyId { get; private set; }

    public Guid? TerminalId { get; private set; }

    /// <summary>Bound at issue. A token presented from a different device is suspect.</summary>
    public string DeviceFingerprint { get; private set; } = null!;

    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public RefreshTokenStatus Status { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsUsable(DateTimeOffset now) =>
        Status == RefreshTokenStatus.Active && ExpiresAt > now;

    public void Consume(Guid replacementId, DateTimeOffset now)
    {
        Status = RefreshTokenStatus.Used;
        UsedAt = now;
        ReplacedByTokenId = replacementId;
    }

    public void Revoke() => Status = RefreshTokenStatus.Revoked;
}

public enum RefreshTokenStatus
{
    Active = 0,
    Used = 1,

    /// <summary>Invalidated by family revocation — typically reuse detection.</summary>
    Revoked = 2
}
