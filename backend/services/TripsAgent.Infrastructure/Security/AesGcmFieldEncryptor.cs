using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TripsAgent.Application.Security;

namespace TripsAgent.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryption under a ring of keys held in configuration (issue 104).
/// </summary>
/// <remarks>
/// <para>
/// Stored layout, one byte string per value:
/// <c>[1 byte format = 2][1 byte key id length][key id, ASCII][12 byte nonce][16 byte tag][ciphertext]</c>.
/// Format 2 so a value can never be mistaken for <see cref="AesGcmSecretProtector"/>'s format 1,
/// which is what passport numbers were stored in before this existed.
/// </para>
/// <para>
/// GCM because it is authenticated: a value altered in the table fails to decrypt rather than
/// decrypting to the wrong passport number. The nonce is random for every value. The associated data
/// binds the key id and the column, so ciphertext copied from one column into another — or relabelled
/// with another key id — fails loudly.
/// </para>
/// <para>
/// Keys are never written to the database, a log or an exception message. Only their ids are.
/// </para>
/// </remarks>
public sealed partial class AesGcmFieldEncryptor : IFieldEncryptor
{
    /// <summary>AES-256. The only key size accepted.</summary>
    public const int KeyBytes = 32;

    /// <summary>The first byte of every value this class makes.</summary>
    public const byte FormatVersion = 2;

    /// <summary>The longest key id the layout holds.</summary>
    public const int MaxKeyIdLength = 32;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly Dictionary<string, byte[]> _keys;

    /// <param name="activeKeyId">The key new values are encrypted under. Must be one of <paramref name="keys"/>.</param>
    /// <param name="keys">Every key still needed to decrypt, by id — the active one and any being retired.</param>
    public AesGcmFieldEncryptor(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            throw new ArgumentException("Field encryption needs at least one key.", nameof(keys));
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var (id, key) in keys)
        {
            if (!IsValidKeyId(id))
            {
                throw new ArgumentException(
                    $"'{id}' is not a usable key id. Use 1 to {MaxKeyIdLength} letters, digits or underscores.",
                    nameof(keys));
            }

            if (key is null || key.Length != KeyBytes)
            {
                throw new ArgumentException(
                    $"Field encryption key '{id}' must be exactly {KeyBytes} bytes (AES-256); it is {key?.Length ?? 0}.",
                    nameof(keys));
            }

            // Copied, so a caller that zeroes or reuses its buffer cannot change the key under us.
            _keys[id] = (byte[])key.Clone();
        }

        if (string.IsNullOrWhiteSpace(activeKeyId) || !_keys.ContainsKey(activeKeyId))
        {
            throw new ArgumentException(
                $"The active field encryption key id '{activeKeyId}' is not one of the configured keys ("
                + string.Join(", ", _keys.Keys.Order(StringComparer.Ordinal)) + ").",
                nameof(activeKeyId));
        }

        ActiveKeyId = activeKeyId;
        Fingerprint = ComputeFingerprint(activeKeyId, _keys);
    }

    public string ActiveKeyId { get; }

    /// <summary>
    /// A digest of the whole key ring, used only to tell EF Core's model cache that two encryptors are
    /// the same. Never logged; it reveals nothing a SHA-256 does not.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>True when <paramref name="keyId"/> fits the layout and an environment variable name.</summary>
    public static bool IsValidKeyId(string? keyId) => keyId is not null && KeyIdPattern().IsMatch(keyId);

    public byte[] Encrypt(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var keyId = Encoding.ASCII.GetBytes(ActiveKeyId);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var header = 2 + keyId.Length;
        var output = new byte[header + NonceBytes + TagBytes + clear.Length];

        output[0] = FormatVersion;
        output[1] = (byte)keyId.Length;
        keyId.CopyTo(output.AsSpan(2));

        var nonce = output.AsSpan(header, NonceBytes);
        var tag = output.AsSpan(header + NonceBytes, TagBytes);
        var cipher = output.AsSpan(header + NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(_keys[ActiveKeyId], TagBytes);
            aes.Encrypt(nonce, clear, cipher, tag, AssociatedData(ActiveKeyId, purpose));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }

        return output;
    }

    public string Decrypt(byte[] ciphertext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var keyId = KeyIdOf(ciphertext)
            ?? throw new CryptographicException("The value is not one field encryption produced.");

        if (!_keys.TryGetValue(keyId, out var key))
        {
            // The id is safe to name; the key never is.
            throw new CryptographicException(
                $"The value was encrypted under key '{keyId}', which is not configured. A retired key must stay "
                + "configured until every value under it has been re-encrypted (docs/runbooks/field-encryption.md).");
        }

        var header = 2 + keyId.Length;
        var nonce = ciphertext.AsSpan(header, NonceBytes);
        var tag = ciphertext.AsSpan(header + NonceBytes, TagBytes);
        var cipher = ciphertext.AsSpan(header + NonceBytes + TagBytes);
        var clear = new byte[cipher.Length];

        try
        {
            // Throws on tampering, the wrong column or the wrong key. It never returns wrong plaintext.
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(nonce, cipher, tag, clear, AssociatedData(keyId, purpose));
            }

            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string? KeyIdOf(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        if (ciphertext.Length < 2 || ciphertext[0] != FormatVersion)
        {
            return null;
        }

        int length = ciphertext[1];

        if (length is 0 or > MaxKeyIdLength || ciphertext.Length < 2 + length + NonceBytes + TagBytes)
        {
            return null;
        }

        var keyId = Encoding.ASCII.GetString(ciphertext, 2, length);

        return IsValidKeyId(keyId) ? keyId : null;
    }

    private static byte[] AssociatedData(string keyId, string purpose) =>
        Encoding.UTF8.GetBytes($"tripsagent:field:v{FormatVersion}:{keyId}:{purpose}");

    private static string ComputeFingerprint(string activeKeyId, Dictionary<string, byte[]> keys)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(activeKeyId));

        foreach (var (id, key) in keys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.ASCII.GetBytes("|" + id + "="));
            hash.AppendData(key);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
