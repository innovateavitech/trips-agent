using System.Text;
using System.Text.RegularExpressions;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// What a page's address and title may be.
/// </summary>
/// <remarks>
/// A slug becomes part of a public URL on the agent's own domain, so it is lower-case letters, digits
/// and single hyphens — and it may not take an address the storefront itself answers on.
/// </remarks>
public static partial class SitePageRules
{
    /// <summary>The home page's slug. The storefront serves it at <c>/</c>.</summary>
    public const string HomeSlug = "home";

    public const int MaxSlugLength = 60;

    public const int MaxTitleLength = 80;

    public const int MaxMetaTitleLength = 70;

    public const int MaxMetaDescriptionLength = 160;

    /// <summary>The most pages one site may have, system pages included.</summary>
    public const int MaxPages = 20;

    /// <summary>
    /// Addresses the storefront serves itself, so no page may take them. <c>home</c> is here too:
    /// the home page owns it and lives at <c>/</c>.
    /// </summary>
    public static IReadOnlySet<string> ReservedSlugs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HomeSlug, "preview", "media", "api", "sitemap", "robots", "search", "checkout", "cart",
        "account", "login", "manage", "booking", "bookings", "flights", "buses", "_next",
    };

    /// <summary>Normalises and checks a slug, or throws with a reason written for the agent.</summary>
    public static string RequireSlug(string? slug)
    {
        if (!TryNormaliseSlug(slug, out var normalised, out var problem))
        {
            throw new ArgumentException(problem, nameof(slug));
        }

        return normalised;
    }

    /// <summary>Lower-cases and checks a slug. False with a reason when it cannot be used.</summary>
    public static bool TryNormaliseSlug(string? slug, out string normalised, out string problem)
    {
        normalised = string.Empty;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(slug))
        {
            problem = "A page needs an address.";
            return false;
        }

        var candidate = slug.Trim().Trim('/').ToLowerInvariant();

        if (candidate.Length > MaxSlugLength)
        {
            problem = $"A page address can be at most {MaxSlugLength} characters.";
            return false;
        }

        if (!SlugPattern().IsMatch(candidate))
        {
            problem = "Use lower-case letters, numbers and single hyphens only — for example 'group-trips'.";
            return false;
        }

        if (ReservedSlugs.Contains(candidate))
        {
            problem = $"'{candidate}' is used by the site itself. Choose another address.";
            return false;
        }

        normalised = candidate;
        return true;
    }

    /// <summary>A slug made from a title: "Group Trips & Tours!" becomes <c>group-trips-tours</c>.</summary>
    public static string SlugFrom(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var builder = new StringBuilder(title.Length);
        var previousWasHyphen = true;

        foreach (var character in title)
        {
            if (FoldToAscii(character) is { } letter)
            {
                builder.Append(letter);
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return string.IsNullOrEmpty(slug) || ReservedSlugs.Contains(slug) ? "page" : slug;
    }

    /// <summary>Checks a page title, or throws.</summary>
    public static string RequireTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A page needs a title.", nameof(title));
        }

        var trimmed = title.Trim();

        return trimmed.Length <= MaxTitleLength
            ? trimmed
            : throw new ArgumentException($"A page title can be at most {MaxTitleLength} characters.", nameof(title));
    }

    /// <summary>Trims optional text to null, or throws when it is too long.</summary>
    public static string? OptionalText(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"That can be at most {maxLength} characters.", parameterName);
    }

    /// <summary>
    /// A letter or digit as plain ASCII — accents dropped, so "Café" gives <c>cafe</c> rather than <c>caf</c>.
    /// </summary>
    /// <remarks>
    /// A table rather than Unicode decomposition: the hosts run with invariant globalisation, where
    /// decomposition does nothing, and a slug must come out the same on every machine.
    /// </remarks>
    private static char? FoldToAscii(char character) => character switch
    {
        >= 'a' and <= 'z' or >= '0' and <= '9' => character,
        >= 'A' and <= 'Z' => char.ToLowerInvariant(character),
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'À' or 'Á' or 'Â' or 'Ã' or 'Ä' or 'Å' => 'a',
        'ç' or 'Ç' => 'c',
        'è' or 'é' or 'ê' or 'ë' or 'È' or 'É' or 'Ê' or 'Ë' => 'e',
        'ì' or 'í' or 'î' or 'ï' or 'Ì' or 'Í' or 'Î' or 'Ï' => 'i',
        'ñ' or 'Ñ' => 'n',
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'Ò' or 'Ó' or 'Ô' or 'Õ' or 'Ö' or 'Ø' => 'o',
        'ù' or 'ú' or 'û' or 'ü' or 'Ù' or 'Ú' or 'Û' or 'Ü' => 'u',
        'ý' or 'ÿ' or 'Ý' => 'y',
        _ => null,
    };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
