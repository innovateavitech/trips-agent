using System.Collections.Frozen;

namespace TripsAgent.Domain.Suppliers;

/// <summary>What the platform knows about airports: which are in Nigeria, and so on Lagos time.</summary>
/// <remarks>
/// Suppliers send wall-clock times with no offset. A Nigerian airport's clock is Lagos time all year —
/// Nigeria keeps no daylight saving — so an instant stored in UTC is shown back in Lagos time there.
/// Anywhere else we hold no airport-to-zone table, and UTC is shown rather than a guess.
/// </remarks>
public static class Airports
{
    /// <summary>Lagos time, all year.</summary>
    public static readonly TimeSpan NigeriaOffset = TimeSpan.FromHours(1);

    /// <summary>Nigerian airports by IATA code. A new airport is one line here.</summary>
    public static readonly FrozenSet<string> Nigerian = new[]
    {
        "ABB", "ABV", "AKR", "BCU", "BNI", "CBQ", "DKA", "ENU", "GMO", "IBA", "ILR", "JOS", "KAD",
        "KAN", "LOS", "MDI", "MIU", "MXJ", "PHC", "QOW", "QRW", "QUO", "SKO", "YOL",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True for a Nigerian airport's IATA code.</summary>
    public static bool IsNigerian(string? code) =>
        code is not null && Nigerian.Contains(code.Trim().ToUpperInvariant());

    /// <summary>
    /// An instant as the wall clock at <paramref name="airport"/> read it: <c>YYYY-MM-DDTHH:mm</c>, what a
    /// ticket prints. Lagos time at a Nigerian airport; UTC anywhere else.
    /// </summary>
    public static string WallClock(DateTimeOffset at, string? airport) =>
        at.ToOffset(IsNigerian(airport) ? NigeriaOffset : TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
