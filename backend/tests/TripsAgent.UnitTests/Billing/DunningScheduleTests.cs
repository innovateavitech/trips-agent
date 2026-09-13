using FluentAssertions;
using TripsAgent.Domain.Billing;

namespace TripsAgent.UnitTests.Billing;

/// <summary>
/// The retry schedule from issue 65: days 1, 3, 5 and 7 after the first failure, then stop.
/// </summary>
/// <remarks>
/// Worth testing on its own because the thing that goes wrong with a dunning schedule is not the
/// arithmetic — it is measuring from the wrong moment. Measured from the previous attempt, a day
/// the job did not run stretches a one-week schedule into a fortnight, and an agency that stopped
/// paying keeps trading for twice as long as anyone agreed.
/// </remarks>
public class DunningScheduleTests
{
    private static readonly DateTimeOffset FirstFailure = new(2026, 3, 2, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_schedule_is_one_three_five_and_seven_days_after_the_first_failure()
    {
        DunningSchedule.RetryDays.Should().Equal(1, 3, 5, 7);

        DunningSchedule.AllAttemptsFrom(FirstFailure).Should().Equal(
            FirstFailure.AddDays(1),
            FirstFailure.AddDays(3),
            FirstFailure.AddDays(5),
            FirstFailure.AddDays(7));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 7)]
    public void Each_retry_is_measured_from_the_first_failure_not_from_the_last_attempt(int retriesSoFar, int days)
    {
        DunningSchedule.NextAttemptAt(FirstFailure, retriesSoFar)
            .Should().Be(FirstFailure.AddDays(days));
    }

    [Fact]
    public void After_four_retries_there_is_nothing_left_to_try()
    {
        DunningSchedule.MaxRetries.Should().Be(4);

        DunningSchedule.NextAttemptAt(FirstFailure, retriesSoFar: 4).Should().BeNull();
        DunningSchedule.IsExhausted(4).Should().BeTrue();
        DunningSchedule.IsExhausted(3).Should().BeFalse();
    }

    [Fact]
    public void A_schedule_that_starts_late_still_finishes_seven_days_after_the_failure()
    {
        // The job missed two days and runs on day 4. The third retry is still due on day 5, not on
        // day 4 plus five.
        var caughtUp = DunningSchedule.NextAttemptAt(FirstFailure, retriesSoFar: 2);

        caughtUp.Should().Be(FirstFailure.AddDays(5));
    }
}
