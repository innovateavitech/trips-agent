using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>One payment on a booking's schedule: what is owed, and when.</summary>
/// <param name="Sequence">1 for the deposit, or for the first payment when there is no deposit.</param>
/// <param name="Label">What the traveller sees: "Deposit", "Balance", "Payment 2".</param>
/// <param name="DueDate">The day it is owed. Never before the day they booked.</param>
/// <param name="AmountMinor">In minor units. The lines add up to the price exactly.</param>
/// <param name="DueOnBooking">True when it is owed the moment they book — the deposit, or a date already past.</param>
public sealed record ScheduledPayment(
    int Sequence,
    string Label,
    DateOnly DueDate,
    Money AmountMinor,
    bool DueOnBooking);

/// <summary>
/// What a party pays for a departure, and when, from the terms the agent set (build plan F6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole kobo throughout.</b> Each payment is rounded down and the last one takes what is left,
/// so the lines always add up to the price exactly and no kobo is invented or lost. That is the
/// same allocation <see cref="Money.Allocate"/> makes, written out here because the shares are
/// uneven.
/// </para>
/// <para>
/// It is a pure function of the terms, the booking date and the price. The schedule stored against
/// a booking is a snapshot of what this returned on the day — a later edit to the departure's terms
/// must never move a payment somebody has already been told about (CLAUDE.md rule 5).
/// </para>
/// </remarks>
public static class DeparturePaymentPlan
{
    /// <summary>What one traveller pays up front at this seat price.</summary>
    public static Money DepositPerPax(DepartureTerms terms, Money pricePerPaxMinor)
    {
        ArgumentNullException.ThrowIfNull(terms);

        return terms.DepositType switch
        {
            DepositType.Percent => new Money(
                pricePerPaxMinor.AmountMinor * (terms.DepositPercentBasisPoints ?? 0)
                / DepartureRules.FullBasisPoints),

            // A deposit larger than the seat is the whole seat, never more than it.
            DepositType.Fixed when (terms.DepositAmountMinor ?? Money.Zero) > pricePerPaxMinor => pricePerPaxMinor,
            DepositType.Fixed => terms.DepositAmountMinor ?? Money.Zero,

            _ => Money.Zero,
        };
    }

    /// <summary>
    /// The whole schedule for a party of <paramref name="paxCount"/> booking on
    /// <paramref name="bookingDate"/> at <paramref name="pricePerPaxMinor"/>.
    /// </summary>
    /// <remarks>
    /// With no installments the balance is due at the cutoff — the contract's default. A date that
    /// has already passed when they book is due the day they book, never in the past.
    /// </remarks>
    public static IReadOnlyList<ScheduledPayment> Build(
        DepartureTerms terms,
        DateOnly bookingDate,
        int paxCount,
        Money pricePerPaxMinor)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);

        var total = pricePerPaxMinor * paxCount;
        var deposit = DepositPerPax(terms, pricePerPaxMinor) * paxCount;
        var balance = total - deposit;
        var payments = new List<ScheduledPayment>();

        if (deposit.AmountMinor > 0)
        {
            payments.Add(new ScheduledPayment(1, "Deposit", bookingDate, deposit, DueOnBooking: true));
        }

        if (balance.AmountMinor <= 0)
        {
            return payments;
        }

        if (terms.Installments.Count == 0)
        {
            var cutoff = terms.DepartureDate.AddDays(-terms.CutoffDaysBefore);
            var late = cutoff <= bookingDate;

            payments.Add(new ScheduledPayment(
                payments.Count + 1,
                deposit.AmountMinor > 0 ? "Balance" : "Full price",
                late ? bookingDate : cutoff,
                balance,
                late));

            return payments;
        }

        var allocated = 0L;

        for (var index = 0; index < terms.Installments.Count; index++)
        {
            var item = terms.Installments[index];
            var last = index == terms.Installments.Count - 1;

            // The last payment takes the remainder, so rounding down the others cannot lose a kobo.
            var amount = last
                ? balance.AmountMinor - allocated
                : balance.AmountMinor * item.PercentOfBalanceBasisPoints / DepartureRules.FullBasisPoints;

            allocated += amount;

            var due = item.DueBasis == InstallmentDueBasis.FromBooking
                ? bookingDate.AddDays(item.DueOffsetDays)
                : terms.DepartureDate.AddDays(-item.DueOffsetDays);

            var late = due <= bookingDate;

            payments.Add(new ScheduledPayment(
                payments.Count + 1,
                $"Payment {index + 1}",
                late ? bookingDate : due,
                new Money(amount),
                late));
        }

        return payments;
    }
}
