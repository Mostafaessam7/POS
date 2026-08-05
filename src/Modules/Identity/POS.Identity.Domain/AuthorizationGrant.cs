using POS.SharedKernel;

namespace POS.Identity.Domain;

/// <summary>
/// A manager's approval of ONE specific action by another user.
/// </summary>
/// <remarks>
/// A first-class POS flow with no equivalent in ordinary business software: a
/// cashier attempts a discount beyond their limit and a manager authorises that
/// single action without ending the cashier's session.
///
/// Modelled as a single-use grant against a specific command and its parameters —
/// NOT as temporary permission elevation. Elevation is far easier to get wrong
/// (it leaks past the intended action, it is hard to bound in time) and much
/// harder to audit. A grant naturally records both principals, which is what the
/// transaction needs and what makes override-frequency-per-manager a usable fraud
/// indicator from day one.
///
/// Must work offline. See ADR 016 for the signed-bundle mechanism.
/// </remarks>
public sealed class AuthorizationGrant : AggregateRoot<Guid>, ITenantScoped
{
    private AuthorizationGrant() { }

    public static AuthorizationGrant Issue(
        Guid requestedByUserId,
        Guid approvedByUserId,
        Guid terminalId,
        string permissionCode,
        string commandType,
        string commandFingerprint,
        string reason,
        DateTimeOffset now,
        TimeSpan validFor) => new()
        {
            Id = SequentialId.New(),
            RequestedByUserId = requestedByUserId,
            ApprovedByUserId = approvedByUserId,
            TerminalId = terminalId,
            PermissionCode = permissionCode,
            CommandType = commandType,
            CommandFingerprint = commandFingerprint,
            Reason = reason,
            IssuedAt = now,
            ExpiresAt = now.Add(validFor),
            IsConsumed = false
        };

    public Guid TenantId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public Guid ApprovedByUserId { get; private set; }
    public Guid TerminalId { get; private set; }
    public string PermissionCode { get; private set; } = null!;
    public string CommandType { get; private set; } = null!;

    /// <summary>
    /// Hash of the command's material parameters.
    /// </summary>
    /// <remarks>
    /// Without this, a grant approving "20% off line 3" could be replayed against
    /// "90% off the whole basket". The fingerprint binds approval to what was
    /// actually shown to the manager.
    /// </remarks>
    public string CommandFingerprint { get; private set; } = null!;

    /// <summary>Mandatory. Override frequency and reasons are a fraud report.</summary>
    public string Reason { get; private set; } = null!;

    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool IsConsumed { get; private set; }

    /// <summary>True when the sale was authorised while the terminal was offline.</summary>
    public bool ApprovedOffline { get; private set; }

    public Result Consume(string commandFingerprint, DateTimeOffset now)
    {
        if (IsConsumed)
            return Error.Conflict("grant.consumed", "This authorisation has already been used.");

        if (ExpiresAt <= now)
            return Error.Conflict("grant.expired", "This authorisation has expired.");

        if (!string.Equals(CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
            return Error.Forbidden("grant.mismatch", "Authorisation does not match this operation.");

        IsConsumed = true;
        return Result.Success();
    }

    public void MarkApprovedOffline() => ApprovedOffline = true;
}
