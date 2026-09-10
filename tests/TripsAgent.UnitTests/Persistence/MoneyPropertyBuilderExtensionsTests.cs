using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Persistence.Conventions;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// The explicit spelling of the money mapping, for the cases the convention cannot see.
///
/// <see cref="MoneyMinorConvention"/> keys off the <c>Minor</c> suffix, which covers almost
/// everything. When a column is money but is not named that way, the entity configuration has
/// to say so — and saying so in the configuration keeps the intent where a reviewer will look
/// for it, rather than leaving a bare <c>long</c> that could be a count.
/// </summary>
public class MoneyPropertyBuilderExtensionsTests
{
    [Fact]
    public void IsMoneyMinor_should_map_a_long_to_bigint()
    {
        var property = ModelHarness.Property<LegacyInvoice>(
            nameof(LegacyInvoice.Total),
            entity => entity.Property(invoice => invoice.Total).IsMoneyMinor());

        property.GetColumnType().Should().Be(MoneyMinorConvention.MoneyColumnType);
    }

    [Fact]
    public void IsMoneyMinor_should_map_a_nullable_long_to_bigint()
    {
        var property = ModelHarness.Property<LegacyInvoice>(
            nameof(LegacyInvoice.Settled),
            entity => entity.Property(invoice => invoice.Settled).IsMoneyMinor());

        property.GetColumnType().Should().Be(MoneyMinorConvention.MoneyColumnType);
        property.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void An_unmarked_long_should_still_be_a_64_bit_column()
    {
        // Nothing here is wrong — long already maps to bigint. The helper is about making the
        // intent explicit, not about changing the storage type.
        var property = ModelHarness.Property<LegacyInvoice>(nameof(LegacyInvoice.Total));

        property.ClrType.Should().Be<long>();
    }
}
