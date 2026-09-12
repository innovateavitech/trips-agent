using FluentAssertions;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>The status poller's back-off (#37): 30s → 1m → 2m → 5m → 15m → 30m → 1h, up to the limit plus a buffer.</summary>
public class SupplierPollScheduleTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_gaps_grow_as_the_issue_says_and_the_last_one_repeats()
    {
        var gaps = Enumerable.Range(0, 9)
            .Select(polls => SupplierPollSchedule.Next(polls, At, ticketTimeLimit: null) - At)
            .ToList();

        gaps.Should().Equal(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));
    }

    [Fact]
    public void Near_the_limit_the_next_poll_is_pulled_in_to_the_limit_plus_its_buffer()
    {
        var limit = At.AddMinutes(10);

        var next = SupplierPollSchedule.Next(pollsSoFar: 6, At, limit);

        next.Should().Be(limit + SupplierPollSchedule.TicketTimeLimitBuffer, "an hour's gap would miss the deadline by twenty minutes");
    }

    [Fact]
    public void Past_the_limit_and_its_buffer_polling_carries_on_hourly()
    {
        var limit = At.AddHours(-2);

        SupplierPollSchedule.Next(pollsSoFar: 6, At, limit).Should().Be(At.AddHours(1));
        SupplierPollSchedule.IsPastDeadline(limit, At).Should().BeTrue();
        SupplierPollSchedule.IsPastDeadline(At, At).Should().BeFalse();
        SupplierPollSchedule.IsPastDeadline(ticketTimeLimit: null, At).Should().BeFalse();
    }

    [Fact]
    public void The_poller_waits_longer_than_any_issue_call_may_take_before_taking_over()
    {
        SupplierPollSchedule.IssueRecoveryDelay.Should().BeGreaterThan(TimeSpan.FromSeconds(45));
    }
}
