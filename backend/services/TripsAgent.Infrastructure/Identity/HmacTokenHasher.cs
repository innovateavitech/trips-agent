using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Identity;

namespace TripsAgent.Infrastructure.Identity;

/// <summary>
/// Hashes generated secrets with HMAC-SHA256 under a server-side key.
/// </summary>
/// <remarks>
/// <para>
/// The key is what makes a six-digit code safe to store. A plain SHA-256 of "048213" can be
/// reversed by hashing all million candidates in well under a second; an HMAC cannot be, unless
/// the attacker also has the key — and the key lives in configuration, never in the database.
/// </para>
/// <para>
/// Deterministic by design, unlike <see cref="Argon2PasswordHasher"/>: the verification flow has
/// to find a stored hash by recomputing it, which a random salt per value would make impossible.
/// </para>
/// </remarks>
public sealed class HmacTokenHasher : ITokenHasher
{
    /// <summary>256 bits. Shorter keys weaken the HMAC; there is no benefit to going longer.</summary>
    public const int MinimumKeyBytes = 32;

    private readonly byte[] _key;

    public HmacTokenHasher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentException(
                $"The token hashing key must be at least {MinimumKeyBytes} bytes; it is {key.Length}.",
                nameof(key));
        }

        _key = key;
    }

    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        return Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(token)));
    }

    public string GenerateNumericCode(int digits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);

        // RandomNumberGenerator, not Random: System.Random is predictable from its output, and a
        // predictable verification code is no verification at all.
        var upperBound = (int)Math.Pow(10, digits);
        var value = RandomNumberGenerator.GetInt32(0, upperBound);

        // Leading zeros kept, so every code is the same length and "007291" is not shown as "7291".
        return value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    public string GenerateOpaqueToken()
    {
        // 256 bits of entropy, base64url so it survives being put in a link unescaped.
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
