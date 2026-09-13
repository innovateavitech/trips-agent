using System.Text.Json;
using FluentAssertions;
using TripsAgent.Contracts.Analytics;

namespace TripsAgent.UnitTests.Analytics;

/// <summary>
/// Margin withheld means the field is not in the JSON at all.
/// </summary>
/// <remarks>
/// A <c>"marginMinor": null</c> still tells the reader the field exists and was hidden from them,
/// and a client that treats null as zero shows an agent a margin of nothing. The contract drops the
/// property instead, and this pins it with the same serializer defaults the API uses.
/// </remarks>
public class MarginRedactionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_day_without_margin_view_has_no_margin_properties()
    {
        var day = new AgencyDayResponse(
            new DateOnly(2026, 3, 10), "NGN", 1, 1, 110_750, 0, 0, 0, 0, null, null, null);

        var json = JsonSerializer.Serialize(day, Web);

        json.Should().NotContain("netCostMinor").And.NotContain("markupMinor").And.NotContain("marginMinor");
        json.Should().Contain("\"grossSalesMinor\":110750");
    }

    [Fact]
    public void A_day_with_margin_view_carries_them()
    {
        var day = new AgencyDayResponse(
            new DateOnly(2026, 3, 10), "NGN", 1, 1, 110_750, 0, 0, 0, 0, 100_000, 10_000, 9_500);

        var json = JsonSerializer.Serialize(day, Web);

        json.Should().Contain("\"marginMinor\":9500").And.Contain("\"netCostMinor\":100000");
    }

    [Fact]
    public void A_drill_down_row_without_margin_view_has_no_cost_or_margin()
    {
        var row = new BookingRowResponse(
            Guid.Empty, Guid.Empty, "ORD-1", new DateOnly(2026, 3, 10), DateTimeOffset.UnixEpoch,
            "Flight", "Console", "LOS → ABV", "Confirmed", "Confirmed", "NGN", 110_750, null, null);

        var json = JsonSerializer.Serialize(row, Web);

        json.Should().NotContain("netAmountMinor").And.NotContain("marginMinor");
    }
}
