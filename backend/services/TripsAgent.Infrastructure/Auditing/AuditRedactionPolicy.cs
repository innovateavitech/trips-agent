namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// Decides, by property name, what the audit log may record.
///
/// The audit log is the one table that keeps a copy of every change, forever. That makes it the
/// worst possible place for a secret: a password hash written here outlives the rotation that
/// was supposed to retire it, and a passport number written here outlives the traveller's
/// relationship with the agency. So some values never arrive, and some arrive with most of
/// themselves removed.
///
/// Matching is on the property name, case-insensitively, by substring — <c>PasswordHash</c>,
/// <c>hashed_password</c> and <c>NewPassword</c> all match <c>password</c>. Substrings rather
/// than exact names because the failure mode matters: a name nobody predicted should still be
/// caught, and over-redacting costs a column of an audit row while under-redacting costs a
/// credential.
///
/// <para>
/// <b>This is a deny list, and a deny list only knows what it was told.</b> Adding a new
/// sensitive field to an entity means adding its name here in the same pull request. The
/// alternative — recording nothing unless explicitly allowed — was rejected because an audit
/// log that is empty by default gets "fixed" by allowing everything.
/// </para>
/// </summary>
public sealed class AuditRedactionPolicy
{
    /// <summary>Stands in for a value that is never recorded.</summary>
    public const string RedactedPlaceholder = "[redacted]";

    /// <summary>How many trailing characters a masked value keeps.</summary>
    public const int MaskedSuffixLength = 4;

    /// <summary>
    /// Credentials and secrets. None of these has any business in an audit trail: knowing that
    /// a password changed is the audited fact, and the value itself adds nothing to it.
    /// </summary>
    private static readonly string[] SecretFragments =
    [
        "password", "passphrase", "secret", "salt", "token", "apikey", "api_key",
        "privatekey", "private_key", "signature", "otp", "securityanswer", "security_answer",
        "cvv", "cvc", "pin", "merchantkey", "merchant_key", "clientsecret", "client_secret",
    ];

    /// <summary>
    /// Identity and financial document numbers. An auditor needs to see <i>which</i> passport a
    /// change referred to, which the last few digits answer. Nobody needs the whole number.
    /// </summary>
    private static readonly string[] MaskedFragments =
    [
        "passport", "bvn", "nin", "nationalid", "national_id", "accountnumber", "account_number",
        "cardnumber", "card_number", "pan", "iban", "taxid", "tax_id", "tin", "licencenumber",
        "licensenumber", "license_number", "licence_number",
    ];

    private readonly string[] secrets;
    private readonly string[] masked;

    /// <summary>Creates the policy, optionally extending it for entities with unusual names.</summary>
    /// <param name="additionalSecrets">Extra name fragments to redact entirely.</param>
    /// <param name="additionalMasked">Extra name fragments to record only the tail of.</param>
    public AuditRedactionPolicy(
        IEnumerable<string>? additionalSecrets = null,
        IEnumerable<string>? additionalMasked = null)
    {
        secrets = [.. SecretFragments, .. Normalise(additionalSecrets)];
        masked = [.. MaskedFragments, .. Normalise(additionalMasked)];
    }

    /// <summary>Works out how a property may be recorded.</summary>
    public AuditFieldTreatment TreatmentFor(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        // Secrets win over masking: a property called "passport_token" is a token first.
        if (MatchesAny(propertyName, secrets))
        {
            return AuditFieldTreatment.Redact;
        }

        return MatchesAny(propertyName, masked) ? AuditFieldTreatment.Mask : AuditFieldTreatment.Keep;
    }

    /// <summary>Applies the policy, returning what should actually be written.</summary>
    public object? Apply(string propertyName, object? value) =>
        TreatmentFor(propertyName) switch
        {
            AuditFieldTreatment.Redact => RedactedPlaceholder,
            AuditFieldTreatment.Mask => Mask(value),
            _ => value,
        };

    /// <summary>
    /// Keeps the last few characters and replaces the rest, preserving the original length so
    /// the shape of the value is still recognisable.
    ///
    /// A value too short to mask is redacted outright rather than half-shown: revealing four of
    /// five characters is not masking, it is a hint.
    /// </summary>
    private static string Mask(object? value)
    {
        var text = value?.ToString();

        if (string.IsNullOrEmpty(text))
        {
            return RedactedPlaceholder;
        }

        if (text.Length <= MaskedSuffixLength)
        {
            return RedactedPlaceholder;
        }

        var hidden = text.Length - MaskedSuffixLength;
        return string.Concat(new string('*', hidden), text.AsSpan(hidden));
    }

    private static bool MatchesAny(string propertyName, string[] fragments) =>
        Array.Exists(fragments, fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Normalise(IEnumerable<string>? fragments) =>
        (fragments ?? []).Where(fragment => !string.IsNullOrWhiteSpace(fragment));
}
