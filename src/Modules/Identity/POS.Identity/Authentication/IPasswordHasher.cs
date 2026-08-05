namespace POS.Identity.Authentication;

/// <summary>Hashing behind an interface so the algorithm is a swappable component.</summary>
public interface IPasswordHasher
{
    public HashedPassword Hash(string password);

    /// <summary>
    /// Verifies, and reports whether the stored hash used weaker parameters than
    /// current policy.
    /// </summary>
    /// <remarks>
    /// Returning <c>NeedsUpgrade</c> is what allows cost factors to be raised over
    /// time without a forced password reset: on successful login with an outdated
    /// hash, re-hash and store. Retrofitting this later means either resetting every
    /// password or maintaining two verification paths forever.
    /// </remarks>
    public PasswordVerificationResult Verify(string password, string hash, string algorithm);
}

public sealed record HashedPassword(string Hash, string Algorithm);

public sealed record PasswordVerificationResult(bool IsValid, bool NeedsUpgrade);
