namespace TripsAgent.Domain.Storefront;

/// <summary>
/// Which part of a hostname its owner registered — <c>lekkihorizon.com</c> in <c>www.lekkihorizon.com</c> —
/// without shipping the whole Public Suffix List.
/// </summary>
/// <remarks>
/// <para>
/// Two things need it. A CNAME record cannot sit at the root of a domain, which already holds the zone's
/// own records, so a bare domain is refused and the agent is pointed at <c>www</c>. And most registrars'
/// "Host" field wants a record's name without the domain on the end: typing the full name there is the
/// commonest way a record ends up at <c>www.example.com.example.com</c>.
/// </para>
/// <para>
/// A short list of two-label suffixes covers the registries agents here buy from. One missing from it errs
/// safe: a bare domain under an unlisted suffix would be accepted, and then simply never verify, because its
/// CNAME cannot exist. Nothing is served until it does.
/// </para>
/// </remarks>
public static class RegistrableDomains
{
    /// <summary>Suffixes under which names are registered two labels deep, such as <c>com.ng</c>.</summary>
    public static IReadOnlySet<string> MultiLabelSuffixes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Nigeria first: most agents' domains are here.
        "com.ng", "org.ng", "net.ng", "gov.ng", "edu.ng", "name.ng", "sch.ng", "mil.ng", "mobi.ng",

        // Elsewhere in Africa.
        "com.gh", "org.gh", "co.ke", "or.ke", "co.za", "org.za", "co.tz", "co.ug", "com.eg",

        // Where agents abroad register.
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "com.au", "co.nz", "co.in", "com.br", "co.jp",
    };

    /// <summary>
    /// The registered part of <paramref name="hostname"/>: its last two labels, or its last three under a
    /// listed suffix. A hostname that short is returned whole.
    /// </summary>
    public static string Of(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        var labels = hostname.Split('.');

        if (labels.Length <= 2)
        {
            return hostname;
        }

        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        var take = MultiLabelSuffixes.Contains(lastTwo) ? 3 : 2;

        return string.Join('.', labels[^take..]);
    }

    /// <summary>True when <paramref name="hostname"/> is a bare registered domain, or a suffix itself.</summary>
    public static bool IsApex(string hostname) =>
        MultiLabelSuffixes.Contains(hostname) || string.Equals(Of(hostname), hostname, StringComparison.Ordinal);

    /// <summary>
    /// What a registrar's "Host" field wants for <paramref name="recordName"/>: the name without the
    /// registered domain on the end, or <c>@</c> for the domain itself.
    /// </summary>
    /// <param name="recordName">The record's full name, such as <c>_storefront-verify.www.example.com</c>.</param>
    /// <param name="hostname">The hostname the record is for; its registered domain is what is trimmed off.</param>
    public static string HostLabel(string recordName, string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordName);

        var registered = Of(hostname);

        if (string.Equals(recordName, registered, StringComparison.Ordinal))
        {
            return "@";
        }

        return recordName.EndsWith("." + registered, StringComparison.Ordinal)
            ? recordName[..^(registered.Length + 1)]
            : recordName;
    }
}
