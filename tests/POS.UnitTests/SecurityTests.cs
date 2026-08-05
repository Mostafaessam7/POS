using System.Security.Cryptography;
using POS.Identity.Authorization;
using POS.Identity.Domain;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// Tests for the two mechanisms where a defect is a security incident rather than
/// a bug: the offline permission bundle (a terminal must not be able to grant
/// itself permissions) and refresh token rotation (a stolen token must not become
/// a persistent backdoor).
/// </summary>
public sealed class OfflinePermissionBundleTests
{
    private static OfflinePermissionBundle SampleBundle(DateTimeOffset now) => new(
        TenantId: Guid.CreateVersion7(),
        BranchId: Guid.CreateVersion7(),
        IssuedAt: now,
        ExpiresAt: now.Add(OfflinePermissionBundle.DefaultLifetime),
        Users:
        [
            new OfflineUserPermissions(
                UserId: Guid.CreateVersion7(),
                DisplayName: "A. Cashier",
                PinHash: "$argon2id$stub",
                PermissionVersion: 3,
                PermissionCodes: ["sales.transaction.create"])
        ]);

    [Fact]
    public void Verify_returns_the_bundle_when_the_signature_matches()
    {
        using var key = RSA.Create(2048);
        var bundle = SampleBundle(DateTimeOffset.UtcNow);

        var verified = SignedBundle.Sign(bundle, key).Verify(key);

        verified.ShouldNotBeNull();
        verified.Users.Single().PermissionCodes.ShouldContain("sales.transaction.create");
    }

    [Fact]
    public void Verify_rejects_a_payload_edited_on_the_terminal()
    {
        // The attack this exists to stop: a till is physically accessible to staff,
        // and the bundle sits on its disk. Granting yourself refund rights must be
        // detectable without any server contact.
        using var key = RSA.Create(2048);
        var signed = SignedBundle.Sign(SampleBundle(DateTimeOffset.UtcNow), key);

        var tampered = signed with
        {
            PayloadJson = signed.PayloadJson.Replace(
                "sales.transaction.create",
                "sales.refund.approve")
        };

        tampered.Verify(key).ShouldBeNull();
    }

    [Fact]
    public void Verify_rejects_a_bundle_signed_by_a_different_key()
    {
        using var serverKey = RSA.Create(2048);
        using var attackerKey = RSA.Create(2048);

        var forged = SignedBundle.Sign(SampleBundle(DateTimeOffset.UtcNow), attackerKey);

        forged.Verify(serverKey).ShouldBeNull();
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("")]
    [InlineData("YWJj")] // valid base64, not a valid signature
    public void Verify_returns_null_rather_than_throwing_on_malformed_input(string signature)
    {
        // An unhandled parse error here is a denial of service against the till's
        // ability to sell, triggered by editing a file on disk.
        using var key = RSA.Create(2048);
        var signed = SignedBundle.Sign(SampleBundle(DateTimeOffset.UtcNow), key);

        Should.NotThrow(() => (signed with { Signature = signature }).Verify(key))
              .ShouldBeNull();
    }

    [Fact]
    public void An_expired_bundle_is_not_usable()
    {
        var issued = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var bundle = SampleBundle(issued);

        bundle.IsUsable(issued.AddHours(23)).ShouldBeTrue();
        bundle.IsUsable(bundle.ExpiresAt).ShouldBeFalse();
        bundle.IsUsable(bundle.ExpiresAt.AddSeconds(1)).ShouldBeFalse();
    }
}

public sealed class RefreshTokenRotationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static RefreshToken Issue(Guid familyId) => RefreshToken.Issue(
        userId: Guid.CreateVersion7(),
        tenantId: Guid.CreateVersion7(),
        tokenHash: "hash-" + Guid.NewGuid().ToString("N"),
        familyId: familyId,
        terminalId: Guid.CreateVersion7(),
        deviceFingerprint: "till-01",
        now: Now,
        lifetime: TimeSpan.FromDays(14));

    [Fact]
    public void A_freshly_issued_token_is_usable()
    {
        Issue(Guid.CreateVersion7()).IsUsable(Now).ShouldBeTrue();
    }

    [Fact]
    public void A_consumed_token_cannot_be_used_again()
    {
        // This is what makes reuse DETECTABLE. If a consumed token stayed usable,
        // a stolen refresh token would be a permanent credential.
        var token = Issue(Guid.CreateVersion7());

        token.Consume(replacementId: Guid.CreateVersion7(), now: Now);

        token.IsUsable(Now).ShouldBeFalse();
        token.UsedAt.ShouldBe(Now);
        token.ReplacedByTokenId.ShouldNotBeNull();
    }

    [Fact]
    public void An_expired_token_is_not_usable()
    {
        var token = Issue(Guid.CreateVersion7());

        token.IsUsable(Now.AddDays(13)).ShouldBeTrue();
        token.IsUsable(Now.AddDays(15)).ShouldBeFalse();
    }

    [Fact]
    public void A_revoked_token_is_not_usable()
    {
        var token = Issue(Guid.CreateVersion7());

        token.Revoke();

        token.IsUsable(Now).ShouldBeFalse();
    }

    [Fact]
    public void Rotation_keeps_the_whole_chain_in_one_family()
    {
        // The family is the unit of revocation. On reuse detection the server
        // revokes every token sharing the FamilyId, which logs out the thief AND
        // the legitimate user — deliberately, because at that point we cannot tell
        // which is which, and the safe answer is to make both re-authenticate.
        var familyId = Guid.CreateVersion7();
        var first = Issue(familyId);
        var second = Issue(familyId);
        var third = Issue(familyId);

        first.Consume(second.Id, Now);
        second.Consume(third.Id, Now.AddMinutes(10));

        new[] { first, second, third }
            .Select(t => t.FamilyId)
            .Distinct()
            .ShouldHaveSingleItem();
    }
}

/// <summary>
/// The named, individually revocable identity behind a call to the tenant-bootstrap
/// endpoint — see <see cref="ProvisioningOperator"/>'s own remarks for why it exists
/// and what it replaces.
/// </summary>
public sealed class ProvisioningOperatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_newly_enrolled_operator_is_active()
    {
        var op = ProvisioningOperator.Enrol("ops-jane", "hash", Now);

        op.IsActive.ShouldBeTrue();
        op.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void Revoking_an_operator_makes_it_inactive()
    {
        var op = ProvisioningOperator.Enrol("ops-jane", "hash", Now);

        op.Revoke(Now.AddDays(1));

        op.IsActive.ShouldBeFalse();
        op.RevokedAt.ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public void Revoking_an_already_revoked_operator_does_not_move_the_timestamp()
    {
        // Idempotent so a retried revoke call — the same retry-after-crash reasoning
        // used everywhere else in this codebase — cannot quietly overwrite the
        // original revocation time with a later one.
        var op = ProvisioningOperator.Enrol("ops-jane", "hash", Now);

        op.Revoke(Now.AddDays(1));
        op.Revoke(Now.AddDays(5));

        op.RevokedAt.ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public void Hashing_the_same_key_twice_produces_the_same_hash()
    {
        ProvisioningOperator.HashKey("some-operator-key")
            .ShouldBe(ProvisioningOperator.HashKey("some-operator-key"));
    }

    [Fact]
    public void Hashing_different_keys_produces_different_hashes()
    {
        ProvisioningOperator.HashKey("key-one")
            .ShouldNotBe(ProvisioningOperator.HashKey("key-two"));
    }

    [Fact]
    public void The_plaintext_key_is_never_recoverable_from_the_hash()
    {
        // Not a meaningful cryptographic assertion on its own (SHA-256 is one-way by
        // construction) — this exists as a canary: if HashKey were ever "simplified"
        // to something reversible (e.g. Base64), this is the test that would catch it.
        var hash = ProvisioningOperator.HashKey("some-operator-key");

        hash.ShouldNotContain("some-operator-key");
    }
}
