using System.Globalization;
using System.Text;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// What counts as a hostname here, and its one canonical spelling.
/// </summary>
/// <remarks>
/// <para>
/// Normalised before anything is compared or stored: lower-case, no trailing dot, and an
/// international name converted to its ASCII (punycode) form. Without that, the platform-wide unique
/// index on hostnames could be dodged with <c>Booking.Zara.com</c>, or with the same name written in
/// a different script.
/// </para>
/// <para>
/// A label that mixes scripts — a Latin name with one Cyrillic letter in it — is refused outright.
/// It is the classic homograph: a traveller cannot see the difference, and an agency's lookalike
/// address is a problem nobody notices until it is used against them.
/// </para>
/// </remarks>
public static class Hostnames
{
    public const int MaxLength = 253;

    public const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>
    /// Normalises a hostname an agent typed and checks it is one. False with a reason when it is not.
    /// </summary>
    public static bool TryNormalise(string? input, out string hostname, out string problem)
    {
        hostname = string.Empty;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            problem = "Enter an address, for example www.yourbusiness.com.";
            return false;
        }

        var candidate = input.Trim().TrimEnd('.').ToLowerInvariant();

        if (candidate.Length == 0 || candidate.Length > MaxLength)
        {
            problem = $"An address can be at most {MaxLength} characters.";
            return false;
        }

        if (candidate.Any(character => character > '\u007F'))
        {
            if (!TryToAscii(candidate, out candidate))
            {
                problem = "That address contains characters we cannot use in a domain name.";
                return false;
            }
        }

        var labels = candidate.Split('.');

        if (labels.Length < 2)
        {
            problem = "An address needs at least two parts, like yourbusiness.com.";
            return false;
        }

        foreach (var label in labels)
        {
            if (!IsValidLabel(label))
            {
                problem = "Each part of an address can use letters, numbers and hyphens, "
                          + "must not start or end with a hyphen, and can be at most 63 characters.";
                return false;
            }

            if (label.StartsWith("xn--", StringComparison.Ordinal) && !IsSingleScript(label))
            {
                problem = "That address mixes letters from different alphabets, which lets one address "
                          + "pass itself off as another. Use a single alphabet.";
                return false;
            }
        }

        // An address ending in a number is an IP address, not a name.
        if (labels[^1].All(char.IsAsciiDigit))
        {
            problem = "Use a domain name, not an IP address.";
            return false;
        }

        hostname = candidate;
        return true;
    }

    /// <summary>
    /// A request's Host header as a lookup key: lower-case, port and trailing dot removed. Null when it
    /// cannot be a hostname at all.
    /// </summary>
    /// <remarks>
    /// The hot path of every storefront request, so no IDNA work: a browser always sends the ASCII
    /// form. It is attacker-controlled input about to become a cache key, so it is bounded and checked
    /// character by character before anything else sees it.
    /// </remarks>
    public static string? NormaliseHostHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength + 6)
        {
            return null;
        }

        var host = raw.Trim();

        // A bracketed IPv6 literal is not a site's hostname.
        if (host.StartsWith('['))
        {
            return null;
        }

        var colon = host.IndexOf(':', StringComparison.Ordinal);

        if (colon >= 0)
        {
            host = host[..colon];
        }

        host = host.TrimEnd('.').ToLowerInvariant();

        if (host.Length == 0 || host.Length > MaxLength)
        {
            return null;
        }

        return host.Split('.').All(IsValidLabel) ? host : null;
    }

    /// <summary>True when <paramref name="hostname"/> is <paramref name="zone"/> or anywhere under it.</summary>
    public static bool IsWithin(string hostname, string zone)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        ArgumentNullException.ThrowIfNull(zone);

        return string.Equals(hostname, zone, StringComparison.Ordinal)
               || hostname.EndsWith("." + zone, StringComparison.Ordinal);
    }

    /// <summary>One RFC 1123 label: 1–63 of <c>[a-z0-9-]</c>, not starting or ending with a hyphen.</summary>
    public static bool IsValidLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        return label.Length is > 0 and <= MaxLabelLength
               && label[0] != '-'
               && label[^1] != '-'
               && label.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
    }

    private static bool TryToAscii(string unicode, out string ascii)
    {
        ascii = string.Empty;

        try
        {
            ascii = Idn.GetAscii(unicode).ToLowerInvariant();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // Globalisation-invariant hosts cannot convert international names. Refusing them is the
            // safe answer: an address we cannot normalise is one we cannot keep unique.
            return false;
        }
    }

    /// <summary>
    /// Decodes a punycode label and checks every letter in it comes from one script. Digits and
    /// hyphens belong to every script.
    /// </summary>
    private static bool IsSingleScript(string asciiLabel)
    {
        string unicode;

        try
        {
            unicode = Idn.GetUnicode(asciiLabel);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        var scripts = new HashSet<Script>();

        foreach (var rune in unicode.EnumerateRunes())
        {
            var script = ScriptOf(rune);

            if (script != Script.Common)
            {
                scripts.Add(script);
            }
        }

        return scripts.Count <= 1;
    }

    private static Script ScriptOf(Rune rune)
    {
        var value = rune.Value;

        return value switch
        {
            >= '0' and <= '9' or '-' => Script.Common,
            <= 0x024F or (>= 0x1E00 and <= 0x1EFF) => Script.Latin,
            >= 0x0370 and <= 0x03FF => Script.Greek,
            (>= 0x0400 and <= 0x052F) or (>= 0x1C80 and <= 0x1C8F) => Script.Cyrillic,
            >= 0x0530 and <= 0x058F => Script.Armenian,
            >= 0x0590 and <= 0x05FF => Script.Hebrew,
            (>= 0x0600 and <= 0x06FF) or (>= 0x0750 and <= 0x077F) => Script.Arabic,
            >= 0x0900 and <= 0x097F => Script.Devanagari,
            >= 0x0E00 and <= 0x0E7F => Script.Thai,
            (>= 0x3040 and <= 0x30FF) => Script.Kana,
            (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) => Script.Han,
            >= 0xAC00 and <= 0xD7AF => Script.Hangul,
            _ => Script.Other,
        };
    }

    private enum Script
    {
        Common,
        Latin,
        Greek,
        Cyrillic,
        Armenian,
        Hebrew,
        Arabic,
        Devanagari,
        Thai,
        Kana,
        Han,
        Hangul,
        Other,
    }
}

/// <summary>
/// The labels no agency may take as its free subdomain, and how a label is compared with them.
/// </summary>
/// <remarks>
/// <para>
/// Open question 20, as decided for the MVP: a reserved word is refused outright; a name that looks
/// like a well-known brand is accepted but set aside — it serves nothing until a platform admin has
/// reviewed it, and an admin alert says it is waiting.
/// </para>
/// <para>
/// Compared by <see cref="Skeleton"/>, so the spellings people use to slip past a denylist —
/// <c>adm1n</c>, <c>supp0rt</c>, <c>tr1ps-agent</c> — land on the same entry as the real word.
/// </para>
/// </remarks>
public static class ReservedHostnames
{
    /// <summary>The list seeded into <c>storefront.reserved_hostname_labels</c>.</summary>
    public static IReadOnlyList<(string Label, string Reason)> BaseLabels { get; } = BuildBaseLabels();

    /// <summary>
    /// A label folded to its lookalike skeleton: common digit swaps undone, <c>rn</c> read as <c>m</c>,
    /// hyphens dropped.
    /// </summary>
    public static string Skeleton(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var builder = new StringBuilder(label.Length);

        foreach (var character in label.ToLowerInvariant())
        {
            var folded = character switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                '-' or '_' or '.' => '\0',
                _ => character,
            };

            if (folded != '\0')
            {
                builder.Append(folded);
            }
        }

        return builder.ToString()
            .Replace("rn", "m", StringComparison.Ordinal)
            .Replace("vv", "w", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="label"/> is, or looks like, one of <paramref name="reserved"/>.</summary>
    public static bool IsReserved(string label, IEnumerable<string> reserved)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(reserved);

        var skeleton = Skeleton(label);

        return reserved.Any(entry => string.Equals(Skeleton(entry), skeleton, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when <paramref name="label"/> looks like one of <paramref name="brands"/> — open question 20's
    /// known-brand list. Such a claim is not refused: it is set aside for a person to review.
    /// </summary>
    /// <remarks>
    /// A longer brand matches anywhere in the label (<c>emirates-deals</c>); a short one only as the
    /// whole label, or <c>uba</c> would flag every agency in <c>dubai</c>. A false match costs a review,
    /// not a refusal, so the list can afford to be cautious.
    /// </remarks>
    public static bool LooksLikeBrand(string label, IEnumerable<string> brands)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(brands);

        var skeleton = Skeleton(label);

        return brands
            .Select(Skeleton)
            .Any(brand => brand.Length >= MinimumContainedBrandLength
                ? skeleton.Contains(brand, StringComparison.Ordinal)
                : string.Equals(skeleton, brand, StringComparison.Ordinal));
    }

    /// <summary>Brands this long or longer are matched anywhere inside a label.</summary>
    public const int MinimumContainedBrandLength = 6;

    /// <summary>
    /// Airlines, travel sites and payment brands a Nigerian traveller would recognise — the names a
    /// squatter would borrow. Seeded into <c>storefront.reserved_hostname_labels</c> as brands.
    /// </summary>
    public static IReadOnlyList<string> KnownBrands { get; } =
    [
        "airpeace", "arikair", "ibomair", "unitednigeria", "overlandairways", "valuejet", "greenafrica",
        "maxair", "emirates", "qatarairways", "etihad", "britishairways", "lufthansa", "klm", "airfrance",
        "turkishairlines", "ethiopianairlines", "kenyaairways", "egyptair", "rwandair", "virginatlantic",
        "saudia", "royalairmaroc", "wakanow", "travelstart", "jumia", "hotelsng", "expedia", "airbnb",
        "tripadvisor", "trivago", "skyscanner", "paystack", "flutterwave", "opay", "palmpay", "moniepoint",
        "interswitch", "gtbank", "zenithbank", "accessbank", "firstbank", "uba", "mastercard",
    ];

    private static List<(string Label, string Reason)> BuildBaseLabels()
    {
        const string Infrastructure = "Infrastructure: a name the platform may need for itself.";
        const string Brand = "The platform's own name, which no agency may present as theirs.";
        const string Mailbox = "An RFC 2142 role mailbox name, which mail providers treat as authoritative.";
        const string Account = "A name that reads as a login or account page — the shape a phishing page takes.";

        string[] infrastructure =
        [
            "www", "api", "app", "apps", "admin", "administrator", "mail", "email", "smtp", "imap", "pop",
            "ftp", "cdn", "assets", "static", "media", "img", "images", "files", "staging", "stage", "dev",
            "test", "demo", "beta", "preview", "status", "docs", "blog", "dashboard", "console", "portal",
            "ns1", "ns2", "dns", "mx", "vpn", "git", "root", "system", "internal", "localhost", "edge",
        ];

        string[] brand = ["trips", "tripsagent", "tripsafrica", "tripsng"];

        string[] mailboxes = ["postmaster", "hostmaster", "webmaster", "abuse", "security", "noc"];

        string[] accounts =
        [
            "support", "help", "billing", "account", "accounts", "login", "signin", "signup", "register",
            "auth", "secure", "verify", "wallet", "payment", "payments", "checkout",
        ];

        return
        [
            .. infrastructure.Select(label => (label, Infrastructure)),
            .. brand.Select(label => (label, Brand)),
            .. mailboxes.Select(label => (label, Mailbox)),
            .. accounts.Select(label => (label, Account)),
        ];
    }
}
