namespace TripsAgent.Application.Security;

/// <summary>
/// Encrypts a secret so it can be stored, and decrypts it when it is needed.
/// </summary>
/// <remarks>
/// <para>
/// For secrets we have to <i>use</i> later — a supplier's merchant key, which keys the price hash —
/// and therefore cannot hash. Passwords and one-time codes are hashed instead, and never come here.
/// </para>
/// <para>
/// <paramref name="purpose"/> (on both methods) binds a ciphertext to the column it was made for. A
/// value encrypted as <c>supplier_credentials.merchant_key</c> will not decrypt as anything else,
/// so ciphertext copied from one column into another fails loudly instead of quietly becoming a
/// different secret.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>. Encrypting the same value twice gives different bytes.</summary>
    public byte[] Protect(string plaintext, string purpose);

    /// <summary>Decrypts a value made by <see cref="Protect"/> for the same purpose.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The value was tampered with, made for another purpose, or made under another key.
    /// </exception>
    public string Unprotect(byte[] protectedValue, string purpose);
}
