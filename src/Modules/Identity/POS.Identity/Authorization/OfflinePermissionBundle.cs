using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace POS.Identity.Authorization;

/// <summary>
/// A signed snapshot of the permissions for every user enrolled at a branch,
/// delivered to a terminal as part of master-data sync.
/// </summary>
/// <remarks>
/// The requirement generic auth designs miss entirely: a DISCONNECTED terminal must
/// still answer "may this cashier approve a refund?"
///
/// The bundle is signed by the server and verified locally against a public key, so
/// a terminal can read permissions but cannot forge or edit them — important,
/// because a till is physically accessible to staff.
///
/// THE HONEST TENSION: online, revocation is instant (ADR 013). Offline, it is
/// bounded by bundle expiry. There is no clever fix; it is the irreducible cost of
/// selling offline. What matters is that it is a bounded, documented business
/// decision with an operational process for terminals offline beyond the window —
/// not something discovered during an incident. See ADR 016.
/// </remarks>
public sealed record OfflinePermissionBundle(
    Guid TenantId,
    Guid BranchId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<OfflineUserPermissions> Users)
{
    /// <summary>
    /// Deliberately short. The window is the maximum time a revoked cashier can keep
    /// selling on a till that never reconnects.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    public bool IsUsable(DateTimeOffset now) => now < ExpiresAt;
}

public sealed record OfflineUserPermissions(
    Guid UserId,
    string DisplayName,
    string PinHash,
    int PermissionVersion,
    IReadOnlyList<string> PermissionCodes);

/// <summary>Detached-signature envelope for the bundle.</summary>
public sealed record SignedBundle(string PayloadJson, string Signature)
{
    /// <remarks>
    /// PSS rather than PKCS#1 v1.5. Both are currently acceptable, but PSS is
    /// randomised and has a security proof; PKCS#1 v1.5 is retained in the
    /// ecosystem mainly for interoperability with old peers. There are no old
    /// peers here — server and terminal are both ours — so there is no reason
    /// to take the weaker option.
    /// </remarks>
    public static SignedBundle Sign(OfflinePermissionBundle bundle, RSA privateKey)
    {
        var json = JsonSerializer.Serialize(bundle);
        var signature = privateKey.SignData(
            Encoding.UTF8.GetBytes(json),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        return new SignedBundle(json, Convert.ToBase64String(signature));
    }

    /// <summary>
    /// Verifies and deserialises. Returns null on ANY failure — a terminal that
    /// cannot verify a bundle must fall back to the last good one, never trust it.
    /// </summary>
    /// <remarks>
    /// The catch is deliberately broad and deliberately not logged as an error
    /// with payload contents. This method's input is attacker-influenced: a till
    /// is physically accessible to staff, and the bundle file sits on its disk.
    /// Malformed base64, truncated JSON, and a mismatched key must all produce
    /// the same quiet "no" rather than an exception that takes down the sync
    /// worker — an unhandled parse error here would be a denial of service
    /// against the terminal's ability to sell, triggered by editing a file.
    /// </remarks>
    public OfflinePermissionBundle? Verify(RSA publicKey)
    {
        try
        {
            if (!Convert.TryFromBase64String(
                    Signature,
                    new byte[Signature.Length],
                    out _))
            {
                return null;
            }

            var isValid = publicKey.VerifyData(
                Encoding.UTF8.GetBytes(PayloadJson),
                Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            return isValid
                ? JsonSerializer.Deserialize<OfflinePermissionBundle>(PayloadJson)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }
}
