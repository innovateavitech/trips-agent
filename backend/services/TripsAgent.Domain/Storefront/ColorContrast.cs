using System.Globalization;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// WCAG contrast between two colours, for the rule that white text on an agency's primary colour
/// must be readable.
/// </summary>
/// <remarks>
/// Buttons, links and the header all put white text on the primary colour. A pale primary would make
/// every call to action on the agent's site unreadable, and the agent would see it only when a
/// customer complained — so it is refused when it is chosen, not discovered on the live site.
/// </remarks>
public static class ColorContrast
{
    /// <summary>WCAG 2 AA for normal-size text.</summary>
    public const double MinimumForText = 4.5;

    /// <summary>The contrast ratio of <paramref name="hex"/> against white, from 1 to 21.</summary>
    public static double RatioAgainstWhite(string hex) => Ratio(hex, "#FFFFFF");

    /// <summary>True when white text on <paramref name="hex"/> meets <see cref="MinimumForText"/>.</summary>
    public static bool PassesWithWhiteText(string hex) => RatioAgainstWhite(hex) >= MinimumForText;

    /// <summary>The WCAG contrast ratio between two <c>#RGB</c> or <c>#RRGGBB</c> colours.</summary>
    public static double Ratio(string firstHex, string secondHex)
    {
        var first = RelativeLuminance(firstHex);
        var second = RelativeLuminance(secondHex);

        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG relative luminance: 0 for black, 1 for white.</summary>
    public static double RelativeLuminance(string hex)
    {
        var (red, green, blue) = Parse(hex);

        return (0.2126 * Linear(red)) + (0.7152 * Linear(green)) + (0.0722 * Linear(blue));
    }

    private static double Linear(int channel)
    {
        var value = channel / 255.0;

        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static (int Red, int Green, int Blue) Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        var digits = hex.Trim().TrimStart('#');

        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(digit => new string(digit, 2)));
        }

        if (digits.Length != 6 || !int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"'{hex}' is not a hex colour.", nameof(hex));
        }

        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }
}
