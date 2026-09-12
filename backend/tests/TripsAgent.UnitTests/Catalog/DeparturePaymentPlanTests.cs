using FluentAssertions;
using TripsAgent.Application.Catalog;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using static TripsAgent.UnitTests.Catalog.DepartureRulesTests;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// What a party pays for a departure, and when. Whole kobo throughout: the lines add up to the
/// price exactly, and no kobo is invented or lost (build plan F6, plan §3 job 11).
/// </summary>
public class DeparturePaymentPlanTests
{
    private static readonly DateOnly Booked = new(2026, 3, 1);
    private static readonly DateOnly Leaves = new(2026, 9, 14);

    [Fact]
    public void With_no_deposit_and_no_installments_the_whole_price_is_due_at_the_cutoff()
    {
        var payments = Build(Terms() with { Installments = [] }, pax: 2);

        payments.Should().ContainSingle();
        payments[0].Label.Should().Be("Full price");
        payments[0].AmountMinor.Should().Be(new Money(200_000));
        payments[0].DueDate.Should().Be(Leaves.AddDays(-14));
        payments[0].DueOnBooking.Should().BeFalse();
    }

    [Fact]
    public void A_percentage_deposit_is_due_the_day_they_book_and_the_balance_follows()
    {
        var payments = Build(
            Terms() with
            {
                DepositType = DepositType.Percent,
                DepositPercentBasisPoints = 2_500,
                Installments = [],
            },
            pax: 2);

        payments.Should().HaveCount(2);
        payments[0].Label.Should().Be("Deposit");
        payments[0].AmountMinor.Should().Be(new Money(50_000));
        payments[0].DueDate.Should().Be(Booked);
        payments[0].DueOnBooking.Should().BeTrue();
        payments[1].Label.Should().Be("Balance");
        payments[1].AmountMinor.Should().Be(new Money(150_000));
    }

    [Fact]
    public void A_fixed_deposit_is_per_traveller_and_never_more_than_the_seat()
    {
        var terms = Terms() with { DepositType = DepositType.Fixed, DepositAmountMinor = new Money(30_000) };

        DeparturePaymentPlan.DepositPerPax(terms, new Money(100_000)).Should().Be(new Money(30_000));
        DeparturePaymentPlan.DepositPerPax(terms, new Money(20_000)).Should().Be(new Money(20_000));

        Build(terms with { Installments = [] }, pax: 3)[0].AmountMinor.Should().Be(new Money(90_000));
    }

    [Fact]
    public void The_installments_add_up_to_the_balance_exactly_whatever_the_rounding()
    {
        // 33.33% twice of 100,001 kobo is 33,330 each, rounded down; the last payment takes what is
        // left, so nothing is lost and nothing is invented.
        var payments = Build(
            Terms() with
            {
                PriceTiers = [Tier(1, null, 100_001)],
                Installments = [Payment(1, 3_333), Payment(2, 3_333), Payment(3, 3_334)],
            },
            pax: 1);

        payments.Sum(payment => payment.AmountMinor.AmountMinor).Should().Be(100_001);
        payments.Select(payment => payment.AmountMinor.AmountMinor).Should().Equal(33_330, 33_330, 33_341);
    }

    [Fact]
    public void A_deposit_and_installments_together_add_up_to_the_whole_price()
    {
        var payments = Build(
            Terms() with
            {
                DepositType = DepositType.Percent,
                DepositPercentBasisPoints = 3_333,
                Installments = [Payment(1, 5_000), Payment(2, 5_000)],
            },
            pax: 3);

        payments.Sum(payment => payment.AmountMinor.AmountMinor).Should().Be(300_000);
        payments.Should().HaveCount(3);
        payments.Select(payment => payment.Sequence).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Dates_are_counted_from_the_booking_or_from_the_departure_as_each_payment_says()
    {
        var payments = Build(
            Terms() with
            {
                Installments =
                [
                    new InstallmentTerms(1, InstallmentDueBasis.FromBooking, 30, 5_000),
                    new InstallmentTerms(2, InstallmentDueBasis.BeforeDeparture, 60, 5_000),
                ],
            },
            pax: 1);

        payments[0].DueDate.Should().Be(Booked.AddDays(30));
        payments[1].DueDate.Should().Be(Leaves.AddDays(-60));
    }

    [Fact]
    public void A_date_that_has_already_passed_is_owed_the_day_they_book_never_in_the_past()
    {
        // Booked six weeks before it leaves, with a payment due sixty days before: already gone.
        var payments = Build(
            Terms() with
            {
                Installments = [new InstallmentTerms(1, InstallmentDueBasis.BeforeDeparture, 60, 10_000)],
            },
            pax: 1,
            bookedOn: Leaves.AddDays(-42));

        payments[0].DueDate.Should().Be(Leaves.AddDays(-42));
        payments[0].DueOnBooking.Should().BeTrue();
    }

    [Fact]
    public void A_deposit_of_the_whole_price_leaves_no_balance_to_split()
    {
        var payments = Build(
            Terms() with { DepositType = DepositType.Percent, DepositPercentBasisPoints = 10_000 },
            pax: 2);

        payments.Should().ContainSingle();
        payments[0].Label.Should().Be("Deposit");
        payments[0].AmountMinor.Should().Be(new Money(200_000));
    }

    // ------------------------------------------------------------------ reminder stages (job 11)

    [Theory]
    [InlineData(-8, null)]
    [InlineData(-7, InstallmentReminderStage.SevenDays)]
    [InlineData(-4, InstallmentReminderStage.SevenDays)]
    [InlineData(-3, InstallmentReminderStage.ThreeDays)]
    [InlineData(-2, InstallmentReminderStage.ThreeDays)]
    [InlineData(-1, InstallmentReminderStage.OneDay)]
    [InlineData(0, InstallmentReminderStage.OneDay)]
    [InlineData(1, InstallmentReminderStage.Overdue)]
    [InlineData(30, InstallmentReminderStage.Overdue)]
    public void Each_day_before_and_after_the_due_date_has_one_reminder(int daysLate, InstallmentReminderStage? expected)
    {
        InstallmentReminders.StageFor(daysLate).Should().Be(expected);
    }

    [Fact]
    public void A_reminder_is_recorded_once_and_a_closer_one_never_reopens_an_earlier_one()
    {
        var schedule = BookingPaymentSchedule.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            "NGN",
            new Money(100_000),
            "Ada Obi",
            "ada@example.test",
            Booked,
            Build(Terms() with { Installments = [] }, pax: 2),
            DateTimeOffset.UtcNow);

        var item = schedule.Items[0];
        var now = DateTimeOffset.UtcNow;

        item.RecordReminder(InstallmentReminderStage.SevenDays, now).Should().BeTrue();
        item.RecordReminder(InstallmentReminderStage.SevenDays, now).Should().BeFalse();
        item.RecordReminder(InstallmentReminderStage.ThreeDays, now).Should().BeTrue();
        item.RecordReminder(InstallmentReminderStage.SevenDays, now).Should().BeFalse("the stages only ever count down");

        item.Flag(now).Should().BeTrue();
        item.Flag(now).Should().BeFalse();

        item.MarkPaid(now).Should().BeTrue();
        item.MarkPaid(now).Should().BeFalse();
        item.RecordReminder(InstallmentReminderStage.Overdue, now).Should().BeFalse("it is paid");
    }

    [Fact]
    public void Cancelling_a_schedule_closes_what_is_owed_and_leaves_what_was_paid()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = BookingPaymentSchedule.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "NGN",
            new Money(100_000),
            "Ada Obi",
            null,
            Booked,
            Build(
                Terms() with { DepositType = DepositType.Percent, DepositPercentBasisPoints = 2_500 },
                pax: 1),
            now);

        schedule.Items[0].MarkPaid(now);
        schedule.Cancel(now);

        schedule.Items[0].State.Should().Be(InstallmentState.Paid);
        schedule.Items.Skip(1).Should().OnlyContain(item => item.State == InstallmentState.Cancelled);
        schedule.OutstandingMinor.Should().Be(Money.Zero);
    }

    private static DepartureTerms Terms() => Sample() with
    {
        DepartureDate = Leaves,
        CutoffDaysBefore = 14,
        PriceTiers = [Tier(1, null, 100_000)],
    };

    private static IReadOnlyList<ScheduledPayment> Build(DepartureTerms terms, int pax, DateOnly? bookedOn = null) =>
        DeparturePaymentPlan.Build(
            terms,
            bookedOn ?? Booked,
            pax,
            DepartureRules.PriceForParty(terms.PriceTiers, pax)!.Value);
}
