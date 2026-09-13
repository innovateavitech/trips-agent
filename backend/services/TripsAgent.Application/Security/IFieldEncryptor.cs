namespace TripsAgent.Application.Security;

/// <summary>
/// Encrypts one column's value — a passport number, a bank account number — so the database only
/// ever holds ciphertext (issue 104).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nobody calls this at a call site.</b> An EF Core value converter applies it to every column
/// marked encrypted, on the way in and on the way out, so a developer writing a new query or a new
/// handler cannot forget it. The columns that get forgotten are the ones that leak.
/// </para>
/// <para>
/// <b>Encrypted columns cannot be searched or filtered.</b> Every value is encrypted with a fresh
/// random nonce, so the same passport number stored twice is two different byte strings, and a
/// <c>WHERE passport_number = …</c> can never match. That is deliberate: deterministic encryption
/// would make equality search possible, and would tell anyone holding the table which travellers
/// share a passport number at the same time. Find a row by what it belongs to — the order, the
/// agency — and read the value from it.
/// </para>
/// <para>
/// <b>Every value carries the id of the key that made it,</b> so keys rotate without a big-bang
/// migration: new writes use the active key, old values still decrypt under the key they name, and a
/// re-encryption pass moves the rest. See <c>docs/runbooks/field-encryption.md</c>.
/// </para>
/// <para>
/// A port with no cloud in it: the cloud is not chosen yet, and a KMS SDK here would quietly choose
/// it. The implementation reads its keys from configuration; a KMS-backed one can replace it.
/// </para>
/// </remarks>
public interface IFieldEncryptor
{
    /// <summary>The id new values are encrypted under.</summary>
    public string ActiveKeyId { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the active key. The same value encrypted twice
    /// gives different bytes.
    /// </summary>
    /// <param name="plaintext">The value, in clear.</param>
    /// <param name="purpose">The column it is for, such as <c>orders.order_travellers.passport_number</c>.
    /// A value made for one column does not decrypt for another.</param>
    public byte[] Encrypt(string plaintext, string purpose);

    /// <summary>Decrypts a value made by <see cref="Encrypt"/> for the same purpose, under any key it still holds.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The value was altered, was made for another column, or names a key this encryptor does not hold.
    /// </exception>
    public string Decrypt(byte[] ciphertext, string purpose);

    /// <summary>The id of the key <paramref name="ciphertext"/> was made under, or null when it is not a value this format recognises.</summary>
    public string? KeyIdOf(byte[] ciphertext);
}
