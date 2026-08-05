using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace POS.Identity.Authentication;

/// <summary>Argon2id password hashing.</summary>
/// <remarks>
/// Chosen over ASP.NET Core Identity's default PBKDF2. PBKDF2 is not broken, but it
/// is cheap to accelerate on GPUs; Argon2id's memory-hardness is not, which is the
/// current OWASP recommendation. See ADR 012.
///
/// THE TRADE-OFF TO WATCH: Argon2id consumes server memory per hash BY DESIGN.
/// Parameters tuned too aggressively turn the login endpoint into a self-inflicted
/// denial of service — 64 MiB × 200 concurrent logins is 12.8 GiB. The defaults
/// below follow current OWASP guidance (19 MiB, 2 iterations, parallelism 1) and
/// MUST be load-tested before production, with authentication endpoints rate
/// limited independently and aggressively.
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKib = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Parameters are embedded so old hashes remain verifiable after a policy change.</summary>
    private static string CurrentAlgorithm => $"argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}";

    public HashedPassword Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism);

        return new HashedPassword(
            $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}",
            CurrentAlgorithm);
    }

    public PasswordVerificationResult Verify(string password, string hash, string algorithm)
    {
        var parts = hash.Split('.', 2);
        if (parts.Length != 2)
            return new PasswordVerificationResult(false, false);

        if (!TryParse(algorithm, out var memory, out var iterations, out var parallelism))
            return new PasswordVerificationResult(false, false);

        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Derive(password, salt, memory, iterations, parallelism);

        // Fixed-time comparison: a naive SequenceEqual leaks the position of the
        // first differing byte through timing.
        var isValid = CryptographicOperations.FixedTimeEquals(actual, expected);

        return new PasswordVerificationResult(
            isValid,
            NeedsUpgrade: isValid && algorithm != CurrentAlgorithm);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };

        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParse(string algorithm, out int memory, out int iterations, out int parallelism)
    {
        memory = iterations = parallelism = 0;

        var segments = algorithm.Split('$');
        if (segments.Length < 3 || segments[0] != "argon2id") return false;

        foreach (var pair in segments[^1].Split(','))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1], out var value)) return false;

            switch (kv[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }

        return memory > 0 && iterations > 0 && parallelism > 0;
    }
}
