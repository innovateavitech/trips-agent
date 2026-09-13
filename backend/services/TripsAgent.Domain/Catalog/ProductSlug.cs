using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// The part of a storefront URL that names a product: <c>/tours/zanzibar-escape</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lower-case letters, digits and single hyphens between them, and unique within one agency. Two
/// agencies can both sell a <c>zanzibar-escape</c>; they are on different storefronts.
/// </para>
/// <para>
/// Built from the agent's own title and nothing else, so a generated slug can never name Trips —
/// the traveller must never see our brand (CLAUDE.md rule 4).
/// </para>
/// </remarks>
public static partial class ProductSlug
{
    /// <summary>The longest slug. Matches the column.</summary>
    public const int MaxLength = 100;

    /// <summary>What a product with no usable title is called until it has one.</summary>
    public const string Untitled = "untitled";

    /// <summary>
    /// Accented Latin letters and the plain letters they are written as in a URL. Written out
    /// rather than left to Unicode normalisation, because the build runs in invariant-globalisation
    /// mode, where normalisation is not something to rely on.
    /// </summary>
    private static readonly Dictionary<char, string> Folds = BuildFolds();

    /// <summary>True when <paramref name="slug"/> is already a well-formed slug.</summary>
    public static bool IsValid(string? slug) =>
        slug is { Length: > 0 and <= MaxLength } && SlugPattern().IsMatch(slug);

    /// <summary>
    /// Turns free text into a slug: "Zanzibar Escape — 5 Days!" becomes <c>zanzibar-escape-5-days</c>.
    /// Empty when nothing usable is left, as with "!!!".
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var slug = new StringBuilder(Math.Min(text.Length, MaxLength));
        var hyphenDue = false;

        foreach (var character in text)
        {
            // Apostrophes vanish rather than splitting a word: "Lagos's" is "lagoss", not "lagos-s".
            if (character is '\'' or '’')
            {
                continue;
            }

            var letters = Fold(character);

            if (letters is null)
            {
                hyphenDue = slug.Length > 0;
                continue;
            }

            if (hyphenDue)
            {
                slug.Append('-');
                hyphenDue = false;
            }

            slug.Append(letters);

            if (slug.Length >= MaxLength)
            {
                break;
            }
        }

        var result = slug.ToString();
        return result[..Math.Min(result.Length, MaxLength)].TrimEnd('-');
    }

    /// <summary>A slug from a product's title, or <see cref="Untitled"/> when the title gives nothing to work with.</summary>
    public static string FromTitle(string? title)
    {
        var slug = Normalise(title);
        return slug.Length > 0 ? slug : Untitled;
    }

    /// <summary><paramref name="baseSlug"/> with <c>-2</c>, <c>-3</c>… on the end, shortened if need be to stay within the limit.</summary>
    public static string WithSuffix(string baseSlug, int number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSlug);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);

        var suffix = "-" + number.ToString(CultureInfo.InvariantCulture);
        var room = MaxLength - suffix.Length;

        return baseSlug[..Math.Min(baseSlug.Length, room)].TrimEnd('-') + suffix;
    }

    /// <summary>
    /// <paramref name="baseSlug"/> if it is free, otherwise the first of <c>-2</c>, <c>-3</c>… that is.
    /// </summary>
    public static string FirstFree(string baseSlug, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var number = 2; ; number++)
        {
            var candidate = WithSuffix(baseSlug, number);

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="slug"/> is <paramref name="baseSlug"/> or one of its numbered
    /// versions — <c>zanzibar-escape-2</c> belongs to <c>zanzibar-escape</c>.
    /// </summary>
    /// <remarks>
    /// So a draft saved again under the same title keeps the slug it already has, rather than
    /// being renumbered because its own slug is "taken".
    /// </remarks>
    public static bool BelongsTo(string slug, string baseSlug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(baseSlug);

        if (slug == baseSlug)
        {
            return true;
        }

        for (var number = 2; number < 10_000; number++)
        {
            var candidate = WithSuffix(baseSlug, number);

            if (candidate == slug)
            {
                return true;
            }

            if (candidate.Length > slug.Length)
            {
                return false;
            }
        }

        return false;
    }

    private static string? Fold(char character)
    {
        if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            return character.ToString();
        }

        if (character is >= 'A' and <= 'Z')
        {
            return ((char)(character + ('a' - 'A'))).ToString();
        }

        if (character == '&')
        {
            return "and";
        }

        return Folds.GetValueOrDefault(character);
    }

    private static Dictionary<char, string> BuildFolds()
    {
        (string Accented, string Plain)[] groups =
        [
            ("àáâãäåāăąÀÁÂÃÄÅĀĂĄ", "a"),
            ("çćĉċčÇĆĈĊČ", "c"),
            ("ďđĎĐ", "d"),
            ("èéêëēĕėęěẹÈÉÊËĒĔĖĘĚẸ", "e"),
            ("ĝğġģĜĞĠĢ", "g"),
            ("ĥħĤĦ", "h"),
            ("ìíîïĩīĭįıịÌÍÎÏĨĪĬĮİỊ", "i"),
            ("ĵĴ", "j"),
            ("ķĶ", "k"),
            ("ĺļľŀłĹĻĽĿŁ", "l"),
            ("ñńņňÑŃŅŇ", "n"),
            ("òóôõöøōŏőọÒÓÔÕÖØŌŎŐỌ", "o"),
            ("ŕŗřŔŖŘ", "r"),
            ("śŝşšṣŚŜŞŠṢ", "s"),
            ("ţťŧŢŤŦ", "t"),
            ("ùúûüũūŭůűųụÙÚÛÜŨŪŬŮŰŲỤ", "u"),
            ("ŵŴ", "w"),
            ("ýÿŷÝŸŶ", "y"),
            ("źżžŹŻŽ", "z"),
        ];

        var folds = new Dictionary<char, string>
        {
            ['æ'] = "ae",
            ['Æ'] = "ae",
            ['œ'] = "oe",
            ['Œ'] = "oe",
            ['ß'] = "ss",
        };

        foreach (var (accented, plain) in groups)
        {
            foreach (var character in accented)
            {
                folds[character] = plain;
            }
        }

        return folds;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
