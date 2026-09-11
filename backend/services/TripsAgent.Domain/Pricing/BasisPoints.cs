using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Pricing;

/// <summary>
/// Percentages as whole basis points (1% = 100), and the one way a share of money is rounded.
/// </summary>
/// <remarks>
/// The markup, the VAT and the platform fee all go through <see cref="Of"/>, so every share of a
/// price rounds the same way. Two roundings that disagree by a kobo is how a stored quote stops
/// adding up.
/// </remarks>
public static class BasisPoints
{
    /// <summary>Basis points in 100%.</summary>
    public const int PerWhole = 10_000;

    /// <summary>
    /// <paramref name="basisPoints"/> of <paramref name="amount"/>, rounded half up to the minor unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly half a kobo rounds up and anything less rounds down: 7.5% of 20 kobo is 1.5 and
    /// becomes 2; 7.5% of 19 kobo is 1.425 and becomes 1. Done in whole numbers, so it is exact.
    /// </para>
    /// <para>
    /// <see cref="Int128"/> for the intermediate product, so a large fare times a large percentage
    /// cannot overflow before the division brings it back down. The final conversion is checked:
    /// a result too big for a <see cref="long"/> throws rather than wrapping to a negative price.
    /// </para>
    /// </remarks>
    public static Money Of(Money amount, int basisPoints)
    {
        if (amount.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount.AmountMinor, "Only a non-negative amount has a share.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(basisPoints);

        var scaled = ((Int128)amount.AmountMinor * basisPoints) + (PerWhole / 2);
        return new Money(checked((long)(scaled / PerWhole)));
    }

    /// <summary>
    /// A rate for a person to read: 1000 is "10%", 750 is "7.5%", 1234 is "12.34%".
    /// </summary>
    public static string Format(int basisPoints)
    {
        var whole = Math.DivRem(basisPoints, 100, out var hundredths);

        if (hundredths == 0)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{whole}%");
        }

        var fraction = Math.Abs(hundredths).ToString("00", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{whole}.{fraction}%");
    }
}
