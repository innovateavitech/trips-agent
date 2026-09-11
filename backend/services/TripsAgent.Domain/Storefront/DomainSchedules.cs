namespace TripsAgent.Domain.Storefront;

/// <summary>
/// How a custom hostname is proven: the records, how often they are looked for, and what counts
/// as a match.
/// </summary>
public static class DomainVerification
{
    /// <summary>
    /// Where the TXT record goes, in front of the agent's hostname. Neutral on purpose: anyone can look
    /// DNS records up, and the agent's zone should not name the platform behind their site.
    /// </summary>
    public const string TxtRecordPrefix = "_storefront-verify";

    /// <summary>The shortest token accepted: 128 bits written as 26 base-32 characters is the floor.</summary>
    public const int MinimumTokenLength = 26;

    /// <summary>A little time for the agent to add the records before the first look.</summary>
    public static TimeSpan FirstCheckDelay { get; } = TimeSpan.FromMinutes(2);

    /// <summary>After this, checking stops and the host is abandoned. Plan §3, job 13.</summary>
    public static TimeSpan GiveUpAfter { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// When to look next: every 5 minutes for the first hour, every 15 until six hours, then hourly.
    /// </summary>
    /// <remarks>
    /// Most agents add both records within minutes of reading the instructions, so the early checks
    /// are frequent; DNS that is still wrong after a day is usually waiting on someone else, so it is
    /// not worth a query every five minutes for a week.
    /// </remarks>
    public static DateTimeOffset NextCheckAfter(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var elapsed = now - startedAt;

        var interval = elapsed < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(5)
            : elapsed < TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromHours(1);

        return now + interval;
    }

    /// <summary>
    /// True when one of the TXT values is exactly the token.
    /// </summary>
    /// <remarks>
    /// TXT data arrives quoted, and a long value is split into quoted strings of up to 255 characters,
    /// so each value is unquoted and re-joined before comparing. Unrelated records at the same name —
    /// another vendor's verification, an SPF record — are simply not the token, never an error.
    /// </remarks>
    public static bool TokenFound(string expectedToken, IEnumerable<string> txtValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);
        ArgumentNullException.ThrowIfNull(txtValues);

        return txtValues.Any(value => string.Equals(UnquoteTxt(value), expectedToken, StringComparison.Ordinal));
    }

    /// <summary>True when one of the CNAME answers is the target, ignoring case and a trailing dot.</summary>
    public static bool TargetFound(string expectedTarget, IEnumerable<string> cnameValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTarget);
        ArgumentNullException.ThrowIfNull(cnameValues);

        var expected = expectedTarget.Trim().TrimEnd('.');

        return cnameValues.Any(value => string.Equals(value.Trim().TrimEnd('.'), expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>"abc" "def"</c> becomes <c>abcdef</c>; an unquoted value is only trimmed.</summary>
    public static string UnquoteTxt(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();

        if (!trimmed.StartsWith('"'))
        {
            return trimmed;
        }

        var builder = new System.Text.StringBuilder(trimmed.Length);
        var inside = false;

        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];

            if (character == '\\' && inside && index + 1 < trimmed.Length)
            {
                builder.Append(trimmed[++index]);
            }
            else if (character == '"')
            {
                inside = !inside;
            }
            else if (inside)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

/// <summary>When a certificate is renewed, and how a failed attempt backs off. Plan §3, job 14.</summary>
public static class CertificateRenewal
{
    /// <summary>Renew once this close to expiry — the plan's T-30 days.</summary>
    public static TimeSpan RenewBefore { get; } = TimeSpan.FromDays(30);

    /// <summary>A new certificate that fails this many times in a row is marked failed and alerted.</summary>
    public const int MaxIssueAttempts = 6;

    /// <summary>A certificate this close to expiry without a renewal is worth waking someone for.</summary>
    public static TimeSpan UrgentWithin { get; } = TimeSpan.FromDays(7);

    /// <summary>15 minutes, then 1, 4, 12 and 24 hours; never more often than daily after that.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(15),
        2 => TimeSpan.FromHours(1),
        3 => TimeSpan.FromHours(4),
        4 => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(24),
    };
}
