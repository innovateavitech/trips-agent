namespace TripsAgent.Domain.Storefront;

/// <summary>
/// How a site's free address is chosen: the agency's slug, or the first numbered variant no one holds.
/// </summary>
/// <remarks>
/// Deterministic rather than failing: the second "Zara Travel" gets <c>zara-travel-2</c>, not an error.
/// The agent never types this address, so a reserved word in the agency's slug is moved aside with a
/// suffix rather than refused.
/// </remarks>
public static class FreeSubdomains
{
    /// <summary>Leaves room for a numeric suffix inside the 63-character label limit.</summary>
    public const int MaxBaseLength = 50;

    /// <summary>How many numbered variants are tried before giving up.</summary>
    public const int MaxCandidates = 50;

    /// <summary>The label a site starts from: the agency's slug, trimmed to length.</summary>
    public static string BaseLabel(string agencySlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agencySlug);

        var label = agencySlug.Trim().ToLowerInvariant();

        if (label.Length > MaxBaseLength)
        {
            label = label[..MaxBaseLength].TrimEnd('-');
        }

        if (!Hostnames.IsValidLabel(label))
        {
            throw new ArgumentException($"'{agencySlug}' cannot become a hostname label.", nameof(agencySlug));
        }

        return label;
    }

    /// <summary>A reserved label moved aside: <c>admin</c> becomes <c>admin-site</c>.</summary>
    public static string MovedAside(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return $"{label}-site";
    }

    /// <summary>The label, then <c>label-2</c>, <c>label-3</c> … in the order they are tried.</summary>
    public static IEnumerable<string> Candidates(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        yield return label;

        for (var number = 2; number <= MaxCandidates; number++)
        {
            yield return $"{label}-{number}";
        }
    }
}
