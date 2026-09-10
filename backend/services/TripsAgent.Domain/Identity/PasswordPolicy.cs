namespace TripsAgent.Domain.Identity;

/// <summary>
/// What makes a password acceptable. FRD §2.1: at least eight characters, at least one number.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the FRD's rule and nothing stricter. Composition rules beyond length — "one
/// symbol, one capital" — push people towards <c>Password1!</c> and a sticky note, and current
/// NIST guidance (SP 800-63B) advises against them. Length is what actually resists guessing.
/// </para>
/// <para>
/// A maximum exists too, but only as a guard: Argon2id is deliberately expensive, and hashing a
/// megabyte "password" on every sign-in attempt is a cheap way to tie up the server.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    /// <summary>Generous for any real passphrase; small enough that hashing it stays cheap.</summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Checks a password against the policy.
    /// </summary>
    /// <returns>
    /// Every rule it breaks, in plain English, so a signup form can show them all at once rather
    /// than making someone fix one problem, resubmit, and discover the next.
    /// </returns>
    public static IReadOnlyList<string> Validate(string? password)
    {
        var problems = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            problems.Add($"Password must be at least {MinimumLength} characters.");
            problems.Add("Password must contain at least one number.");
            return problems;
        }

        if (password.Length < MinimumLength)
        {
            problems.Add($"Password must be at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            problems.Add($"Password must be at most {MaximumLength} characters.");
        }

        if (!password.Any(char.IsAsciiDigit))
        {
            problems.Add("Password must contain at least one number.");
        }

        return problems;
    }

    /// <summary>True when the password meets every rule.</summary>
    public static bool IsValid(string? password) => Validate(password).Count == 0;
}
