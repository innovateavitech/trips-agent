using FluentAssertions;
using TripsAgent.Domain.Common;

namespace TripsAgent.UnitTests.Domain;

/// <summary>
/// Money is the one type in this codebase where a rounding bug is unrecoverable: the ledger is
/// double-entry, so a lost kobo means debits stop equalling credits and nobody can reconcile
/// it afterwards. These tests are deliberately fussy about the edges.
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Zero_is_zero()
    {
        Money.Zero.AmountMinor.Should().Be(0);
        Money.Zero.IsZero.Should().BeTrue();
        Money.Zero.IsNegative.Should().BeFalse();
    }

    [Theory]
    [InlineData(1500, 0, 150_000)]   // ₦1,500.00
    [InlineData(0, 50, 50)]          // 50 kobo
    [InlineData(1, 5, 105)]          // ₦1.05 — not ₦1.50
    [InlineData(-1500, 50, -150_050)]
    public void FromMajor_converts_to_minor_units(long major, int minor, long expected) =>
        Money.FromMajor(major, minor).AmountMinor.Should().Be(expected);

    [Fact]
    public void FromMajor_supports_zero_decimal_currencies()
    {
        // JPY has no minor unit. Passing 100 here would inflate every yen amount a hundredfold.
        Money.FromMajor(1500, 0, minorUnitsPerMajor: 1).AmountMinor.Should().Be(1500);
    }

    [Fact]
    public void FromMajor_rejects_a_minor_part_that_does_not_fit()
    {
        // 100 kobo is one naira, not a valid minor part — this is a typo, not an amount.
        var act = () => Money.FromMajor(10, minor: 100);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Addition_and_subtraction_are_exact()
    {
        var a = Money.FromMajor(1500);
        var b = Money.FromMajor(2, 50);

        (a + b).AmountMinor.Should().Be(150_250);
        (a - b).AmountMinor.Should().Be(149_750);
        (-b).AmountMinor.Should().Be(-250);
    }

    [Fact]
    public void Multiplication_scales_by_a_whole_quantity()
    {
        var fare = Money.FromMajor(45_000);

        (fare * 3).AmountMinor.Should().Be(13_500_000);
        (3 * fare).Should().Be(fare * 3);
    }

    [Fact]
    public void Overflow_throws_rather_than_wrapping_silently()
    {
        var huge = new Money(long.MaxValue);

        var act = () => huge + new Money(1);

        // Wrapping would turn the largest possible balance into a negative one. Loudly wrong
        // beats quietly wrong when the number is money.
        act.Should().Throw<OverflowException>();
    }

    [Theory]
    [InlineData(150_000, 10, 15_000)]      // 10% of ₦1,500.00
    [InlineData(150_000, 7.5, 11_250)]     // 7.5% VAT
    [InlineData(100, 33.333, 33)]          // 33.333 kobo rounds to 33
    [InlineData(101, 50, 51)]              // 50.5 rounds away from zero, not to even
    [InlineData(-101, 50, -51)]            // and away from zero in the negative direction too
    public void Percentage_rounds_half_away_from_zero(long amountMinor, decimal percent, long expected) =>
        new Money(amountMinor).Percentage(percent).AmountMinor.Should().Be(expected);

    [Fact]
    public void Percentage_uses_bankers_rounding_nowhere()
    {
        // Math.Round's default is MidpointRounding.ToEven, which would make this 50, not 51.
        // An accountant expects 51 and so does the FRD.
        new Money(101).Percentage(50m).AmountMinor.Should().Be(51);
    }

    [Fact]
    public void Allocate_distributes_the_remainder_and_never_loses_a_unit()
    {
        var split = Money.FromMajor(10).Allocate(3);

        split.Select(m => m.AmountMinor).Should().Equal(334, 333, 333);
        split.Sum(m => m.AmountMinor).Should().Be(1000, "an allocation must sum back to the original");
    }

    [Fact]
    public void Allocate_handles_negative_amounts()
    {
        var split = new Money(-1000).Allocate(3);

        split.Select(m => m.AmountMinor).Should().Equal(-334, -333, -333);
        split.Sum(m => m.AmountMinor).Should().Be(-1000);
    }

    [Fact]
    public void Allocate_into_one_part_returns_the_whole()
    {
        new Money(999).Allocate(1).Single().AmountMinor.Should().Be(999);
    }

    [Fact]
    public void Allocate_rejects_a_non_positive_part_count()
    {
        var act = () => new Money(100).Allocate(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(150_000, "1500.00")]
    [InlineData(150_050, "1500.50")]
    [InlineData(5, "0.05")]
    [InlineData(0, "0.00")]
    [InlineData(-150_050, "-1500.50")]
    [InlineData(-50, "-0.50")]   // the sign must survive an amount under one major unit
    public void ToString_renders_major_and_minor(long amountMinor, string expected) =>
        new Money(amountMinor).ToString().Should().Be(expected);

    [Fact]
    public void ToString_omits_the_fraction_for_zero_decimal_currencies() =>
        new Money(1500).ToString(minorUnitsPerMajor: 1).Should().Be("1500");

    [Fact]
    public void Comparison_orders_by_amount()
    {
        var small = Money.FromMajor(1);
        var large = Money.FromMajor(2);

        (small < large).Should().BeTrue();
        (large > small).Should().BeTrue();
        (small <= Money.FromMajor(1)).Should().BeTrue();
        (large >= Money.FromMajor(2)).Should().BeTrue();

        new[] { large, small }.Order().Should().Equal(small, large);
    }

    [Fact]
    public void Equality_is_by_amount()
    {
        Money.FromMajor(1500).Should().Be(new Money(150_000));
        Money.FromMajor(1500).Should().NotBe(new Money(150_001));
    }
}
