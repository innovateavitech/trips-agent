using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using TripsAgent.Application.Security;

namespace TripsAgent.Infrastructure.Security;

/// <summary>
/// Builds the field encryptor from configuration: <c>Security:FieldEncryption:ActiveKeyId</c> and one
/// base64 key per id under <c>Security:FieldEncryption:Keys</c>.
/// </summary>
/// <remarks>
/// <para>
/// With no keys configured at all, the result is <see cref="UnconfiguredFieldEncryptor"/>: <c>migrate</c>
/// and <c>seed</c> still run, and the first attempt to read or write an encrypted column fails with a
/// message saying which setting is missing. With keys configured wrongly — a missing active id, a key of
/// the wrong length — it throws at once, because a half-configured key ring is a mistake to hear about
/// before it has encrypted anything.
/// </para>
/// </remarks>
public static class FieldEncryptionConfiguration
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Security:FieldEncryption";

    /// <summary>The id of the key new values are encrypted under.</summary>
    public const string ActiveKeyIdSetting = SectionName + ":ActiveKeyId";

    /// <summary>The section holding one base64 AES-256 key per key id.</summary>
    public const string KeysSection = SectionName + ":Keys";

    /// <summary>Reads the key ring, or returns the unconfigured encryptor when there is none.</summary>
    public static IFieldEncryptor Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var entry in configuration.GetSection(KeysSection).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            try
            {
                keys[entry.Key] = Convert.FromBase64String(entry.Value);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{KeysSection}:{entry.Key} is not valid base64. Generate a key with: openssl rand -base64 32",
                    ex);
            }
        }

        var activeKeyId = configuration[ActiveKeyIdSetting];

        if (keys.Count == 0)
        {
            return string.IsNullOrWhiteSpace(activeKeyId)
                ? UnconfiguredFieldEncryptor.Instance
                : throw new InvalidOperationException(
                    $"{ActiveKeyIdSetting} is '{activeKeyId}', but no key is configured under {KeysSection}. "
                    + $"Set Security__FieldEncryption__Keys__{activeKeyId} to a key made with: openssl rand -base64 32");
        }

        try
        {
            return new AesGcmFieldEncryptor(activeKeyId ?? string.Empty, keys);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Field encryption is misconfigured: {ex.Message}", ex);
        }
        finally
        {
            foreach (var key in keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}

/// <summary>
/// The encryptor for a process with no field encryption keys: it refuses to encrypt or decrypt,
/// saying what to configure.
/// </summary>
/// <remarks>
/// It never falls back to storing a value in clear. A missing key is an outage for the columns it
/// protects, not a reason to stop protecting them.
/// </remarks>
public sealed class UnconfiguredFieldEncryptor : IFieldEncryptor
{
    /// <summary>The one instance.</summary>
    public static UnconfiguredFieldEncryptor Instance { get; } = new();

    private UnconfiguredFieldEncryptor()
    {
    }

    public string ActiveKeyId => throw Missing();

    public byte[] Encrypt(string plaintext, string purpose) => throw Missing();

    public string Decrypt(byte[] ciphertext, string purpose) => throw Missing();

    public string? KeyIdOf(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        // Reading a value's key id needs no key, and the backfill uses it to find work to do.
        return ciphertext.Length >= 2 && ciphertext[0] == AesGcmFieldEncryptor.FormatVersion
                                     && ciphertext[1] is > 0 and <= AesGcmFieldEncryptor.MaxKeyIdLength
                                     && ciphertext.Length > 2 + ciphertext[1]
            ? System.Text.Encoding.ASCII.GetString(ciphertext, 2, ciphertext[1])
            : null;
    }

    private static InvalidOperationException Missing() =>
        new($"""
             No field encryption key is configured, so traveller documents and bank account numbers can be
             neither stored nor read. Generate a key with `openssl rand -base64 32`, then set
             Security__FieldEncryption__ActiveKeyId (an id such as key2026_09) and
             Security__FieldEncryption__Keys__<that id> to the key. Never commit a real one.
             See docs/runbooks/field-encryption.md.
             """);
}
