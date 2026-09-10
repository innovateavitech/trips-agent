namespace TripsAgent.Domain.Common;

/// <summary>
/// An amount of money, held as a whole number of <em>minor units</em> — kobo for NGN,
/// cents for USD. Never a <see cref="decimal"/>, never a <see cref="double"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists at all: floating-point maths loses fractions of a kobo, and in a
/// double-entry ledger a lost kobo means debits stop equalling credits. That is not a
/// rounding annoyance — it is an imbalance nobody can reconcile after the fact.
/// </para>
/// <para>
/// So we store the smallest indivisible unit as a <see cref="long"/> and never divide
/// implicitly. ₦1,500.00 is <c>150_000</c> kobo. The database column is <c>bigint</c> and
/// is named with a <c>_minor</c> suffix so the unit is obvious at the call site and in
/// psql. See CLAUDE.md rule 2.
/// </para>
/// <para>
/// This type deliberately does <b>not</b> carry a currency code. Currency lives on the
/// owning row (an order line, a ledger entry), because mixing currencies is a business
/// decision that belongs to the aggregate, not to arithmetic.
/// </para>
/// </remarks>
public readonly record struct Money(long AmountMinor) : IComparable<Money>
{
    /// <summary>Zero. Use this rather than <c>new Money(0)</c> so intent reads clearly.</summary>
    public static readonly Money Zero = new(0);

    /// <summary>True when the amount is below zero — a refund, a debit, a negative balance.</summary>
    public bool IsNegative => AmountMinor < 0;

    /// <summary>True when the amount is exactly zero.</summary>
    public bool IsZero => AmountMinor == 0;

    /// <summary>
    /// Builds a <see cref="Money"/> from major units and minor units — <c>FromMajor(1500, 0)</c>
    /// is ₦1,500.00. Useful in tests and seed data, where writing 150000 by hand invites typos.
    /// </summary>
    /// <param name="major">The whole-currency part, e.g. 1500 naira.</param>
    /// <param name="minor">The fractional part, 0-99 for a two-decimal currency.</param>
    /// <param name="minorUnitsPerMajor">100 for NGN/USD/EUR. Passed explicitly so zero-decimal
    /// currencies (JPY, KRW) can supply 1 instead of silently being wrong by two orders of magnitude.</param>
    public static Money FromMajor(long major, int minor = 0, int minorUnitsPerMajor = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minorUnitsPerMajor, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minor, minorUnitsPerMajor);

        return new Money(checked((major * minorUnitsPerMajor) + (major < 0 ? -minor : minor)));
    }

    public static Money operator +(Money left, Money right) => new(checked(left.AmountMinor + right.AmountMinor));

    public static Money operator -(Money left, Money right) => new(checked(left.AmountMinor - right.AmountMinor));

    public static Money operator -(Money value) => new(checked(-value.AmountMinor));

    /// <summary>Multiplies by a whole number — three seats at the same fare, for instance.</summary>
    public static Money operator *(Money value, int quantity) => new(checked(value.AmountMinor * quantity));

    /// <inheritdoc cref="op_Multiply(Money, int)" />
    public static Money operator *(int quantity, Money value) => value * quantity;

    public static bool operator <(Money left, Money right) => left.AmountMinor < right.AmountMinor;

    public static bool operator >(Money left, Money right) => left.AmountMinor > right.AmountMinor;

    public static bool operator <=(Money left, Money right) => left.AmountMinor <= right.AmountMinor;

    public static bool operator >=(Money left, Money right) => left.AmountMinor >= right.AmountMinor;

    public int CompareTo(Money other) => AmountMinor.CompareTo(other.AmountMinor);

    /// <summary>
    /// Applies a percentage — a markup or a tax rate — rounding half away from zero, which is
    /// what an accountant expects and what banker's rounding is not.
    /// </summary>
    /// <remarks>
    /// The rate is a <see cref="decimal"/> and that is fine: it is a <i>ratio</i>, not an amount.
    /// The multiplication happens in decimal (28 significant digits, no binary fraction error)
    /// and the result is rounded back to a whole minor unit before it becomes money again, so no
    /// sub-kobo value ever survives into the ledger.
    /// </remarks>
    /// <param name="percent">The percentage to apply, e.g. <c>7.5m</c> for 7.5%.</param>
    public Money Percentage(decimal percent)
    {
        var raw = AmountMinor * percent / 100m;
        return new Money((long)Math.Round(raw, 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Splits into <paramref name="parts"/> as evenly as possible, distributing any remainder
    /// one minor unit at a time across the leading parts so the pieces always sum back to the
    /// original. Splitting ₦10.00 three ways gives 334 + 333 + 333 kobo, never 333 × 3.
    /// </summary>
    public Money[] Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);

        var quotient = Math.DivRem(AmountMinor, parts, out var remainder);
        var step = Math.Sign(remainder);

        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            var extra = i < Math.Abs(remainder) ? step : 0;
            result[i] = new Money(quotient + extra);
        }

        return result;
    }

    /// <summary>
    /// Renders as major.minor for logs and assertions — <c>1500.00</c>. Deliberately has no
    /// currency symbol: traveller-facing formatting is the agent's branding job, not this type's.
    /// </summary>
    public string ToString(int minorUnitsPerMajor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minorUnitsPerMajor, 1);

        var digits = (int)Math.Floor(Math.Log10(minorUnitsPerMajor));

        // Work in absolute values and re-attach the sign at the end. Formatting the parts
        // separately would drop the minus on amounts under one major unit: -50 kobo splits
        // into a major part of 0 (unsigned) and a minor part of -50, printing "0.50".
        var sign = AmountMinor < 0 ? "-" : string.Empty;
        var major = Math.DivRem(Math.Abs(AmountMinor), minorUnitsPerMajor, out var minor);

        var culture = System.Globalization.CultureInfo.InvariantCulture;

        return digits == 0
            ? sign + major.ToString(culture)
            : $"{sign}{major.ToString(culture)}.{minor.ToString(culture).PadLeft(digits, '0')}";
    }

    public override string ToString() => ToString(100);
}
