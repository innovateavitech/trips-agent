namespace TripsAgent.Application.Identity;

/// <summary>Turns a password into something safe to store, and checks one against it.</summary>
/// <remarks>
/// An interface so the algorithm can be replaced without touching a single handler. Password
/// hashing choices age — today's Argon2id parameters are next decade's weak ones — and the
/// replacement should be a new implementation of this, not an edit spread across the codebase.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a password for storage. The result encodes its own parameters and salt.</summary>
    public string Hash(string password);

    /// <summary>
    /// Checks a password against a stored hash.
    /// </summary>
    /// <returns>
    /// Whether it matched, and whether the stored hash used weaker parameters than we now use —
    /// in which case the caller should re-hash and store the result while it has the plaintext.
    /// </returns>
    public (bool Verified, bool NeedsRehash) Verify(string password, string hash);
}
