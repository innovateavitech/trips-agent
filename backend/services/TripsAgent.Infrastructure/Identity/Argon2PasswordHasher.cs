using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using TripsAgent.Application.Identity;

namespace TripsAgent.Infrastructure.Identity;

/// <summary>
/// Hashes passwords with Argon2id.
/// </summary>
/// <remarks>
/// <para>
/// Argon2id rather than PBKDF2 or bcrypt because it is deliberately expensive in <i>memory</i> as
/// well as time, which is what makes GPU and ASIC cracking impractical. It won the Password
/// Hashing Competition and is what OWASP recommends first.
/// </para>
/// <para>
/// The parameters below follow the OWASP cheat sheet's Argon2id baseline: 19 MiB of memory, two
/// iterations, one degree of parallelism. They are encoded into every stored hash, so raising
/// them later does not invalidate existing passwords — <see cref="Verify"/> reports that a hash
/// is out of date and the caller re-hashes it while it still has the plaintext.
/// </para>
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    /// <summary>Memory cost in kibibytes. 19 MiB, per the OWASP baseline.</summary>
    private const int MemorySizeKb = 19 * 1024;

    /// <summary>Time cost — how many passes over that memory.</summary>
    private const int Iterations = 2;

    /// <summary>How many lanes run in parallel.</summary>
    private const int DegreeOfParallelism = 1;

    private const int SaltLength = 16;
    private const int HashLength = 32;

    /// <summary>Identifies the format so a future scheme can be told apart from this one.</summary>
    private const string Prefix = "argon2id";

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt, MemorySizeKb, Iterations, DegreeOfParallelism);

        // Self-describing: everything needed to verify, and to notice the parameters moved on.
        return string.Join('$',
            Prefix,
            MemorySizeKb.ToString(CultureInfo.InvariantCulture),
            Iterations.ToString(CultureInfo.InvariantCulture),
            DegreeOfParallelism.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public (bool Verified, bool NeedsRehash) Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hash))
        {
            return (false, false);
        }

        var parts = hash.Split('$');

        if (parts.Length != 6
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var memory)
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var iterations)
            || !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var parallelism))
        {
            // An unreadable hash is a failed verification, not an exception. A malformed row
            // must not become a way to crash the sign-in endpoint.
            return (false, false);
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expected = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        var actual = Derive(password, salt, memory, iterations, parallelism);

        // Fixed-time comparison: a byte-by-byte one leaks how much of the hash matched, which is
        // enough to reconstruct it a byte at a time.
        var verified = CryptographicOperations.FixedTimeEquals(actual, expected);

        var needsRehash = verified
            && (memory < MemorySizeKb || iterations < Iterations || parallelism < DegreeOfParallelism);

        return (verified, needsRehash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(HashLength);
    }
}
