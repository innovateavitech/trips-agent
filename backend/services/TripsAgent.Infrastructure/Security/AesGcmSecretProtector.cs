using System.Security.Cryptography;
using System.Text;
using TripsAgent.Application.Security;

namespace TripsAgent.Infrastructure.Security;

/// <summary>
/// Encrypts secrets at rest with AES-256-GCM under a key held in configuration, never in the database.
/// </summary>
/// <remarks>
/// <para>
/// GCM rather than CBC because it is <i>authenticated</i>: a stored value that has been altered — by
/// a bad restore, a hand edit in psql, or someone who can write to the table but does not have the
/// key — fails to decrypt instead of decrypting to something wrong. For a merchant key, "wrong" means
/// every price hash fails and bookings stop; loud and immediate is the better failure.
/// </para>
/// <para>
/// Stored layout, so a future key rotation can tell old values from new:
/// <c>[1 byte format version][12 byte nonce][16 byte tag][ciphertext]</c>. The nonce is random per
/// value, so encrypting the same key twice gives different bytes and equal ciphertexts reveal nothing.
/// </para>
/// <para>
/// The purpose string is the associated data: authenticated but not encrypted. A value encrypted for
/// one column does not decrypt for another. Deliberately built on the framework's
/// <see cref="AesGcm"/> rather than a cloud KMS SDK — the cloud is not chosen yet (CLAUDE.md), and
/// this sits behind <see cref="ISecretProtector"/> so a KMS-backed implementation can replace it.
/// </para>
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    /// <summary>AES-256. The only key size accepted.</summary>
    public const int KeyBytes = 32;

    private const byte FormatVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeyBytes)
        {
            throw new ArgumentException(
                $"The secret encryption key must be exactly {KeyBytes} bytes (AES-256); it is {key.Length}.",
                nameof(key));
        }

        // Copied, so a caller that zeroes or reuses its buffer cannot change the key under us.
        _key = (byte[])key.Clone();
    }

    public byte[] Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var clear = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[HeaderBytes + clear.Length];

        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceBytes);
        var tag = output.AsSpan(1 + NonceBytes, TagBytes);
        var cipher = output.AsSpan(HeaderBytes);

        RandomNumberGenerator.Fill(nonce);

        // A new instance per call: cheap, and it keeps this type free of shared mutable state.
        using (var aes = new AesGcm(_key, TagBytes))
        {
            aes.Encrypt(nonce, clear, cipher, tag, AssociatedData(purpose));
        }

        CryptographicOperations.ZeroMemory(clear);
        return output;
    }

    public string Unprotect(byte[] protectedValue, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (protectedValue.Length < HeaderBytes || protectedValue[0] != FormatVersion)
        {
            throw new CryptographicException("The value is not a secret this protector produced.");
        }

        var nonce = protectedValue.AsSpan(1, NonceBytes);
        var tag = protectedValue.AsSpan(1 + NonceBytes, TagBytes);
        var cipher = protectedValue.AsSpan(HeaderBytes);
        var clear = new byte[cipher.Length];

        try
        {
            // Throws AuthenticationTagMismatchException — a CryptographicException — on tampering,
            // the wrong purpose or the wrong key. It never returns wrong plaintext.
            using (var aes = new AesGcm(_key, TagBytes))
            {
                aes.Decrypt(nonce, cipher, tag, clear, AssociatedData(purpose));
            }

            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static byte[] AssociatedData(string purpose) => Encoding.UTF8.GetBytes($"tripsagent:v{FormatVersion}:{purpose}");
}
