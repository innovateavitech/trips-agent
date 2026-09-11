using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Payments;

namespace TripsAgent.UnitTests.Payments;

/// <summary>
/// The rules about a recorded discrepancy, which mostly come down to: never lose the record, and
/// never cry wolf.
/// </summary>
public class ReconciliationExceptionTests
{
    private static ReconciliationException Unbalanced(
        long expectedMinor = 150_000,
        long actualMinor = 140_000) =>
        ReconciliationException.Record(
            ReconciliationCheck.UnbalancedTransaction,
            "01a08e36-640b-7a9a-936a-8e09a5fcca8e",
            "Transaction group does not balance.",
            new Money(expectedMinor),
            new Money(actualMinor),
            null,
            DateTimeOffset.UtcNow);

    [Fact]
    public void A_new_exception_starts_open_and_seen_once()
    {
        var exception = Unbalanced();

        exception.Status.Should().Be(ReconciliationStatus.Open);
        exception.TimesSeen.Should().Be(1);
        exception.LastSeenAt.Should().BeNull();
        exception.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void The_difference_is_signed_so_a_shortfall_and_a_surplus_are_distinguishable()
    {
        // Credits fell short of debits by ₦100.00.
        Unbalanced(expectedMinor: 150_000, actualMinor: 140_000)
            .DifferenceMinor.AmountMinor.Should().Be(-10_000);

        // And the other way round, which is a different problem with a different cause.
        Unbalanced(expectedMinor: 140_000, actualMinor: 150_000)
            .DifferenceMinor.AmountMinor.Should().Be(10_000);
    }

    [Theory]
    [InlineData(ReconciliationCheck.UnbalancedTransaction, ReconciliationSeverity.P1)]
    [InlineData(ReconciliationCheck.WalletBalanceDrift, ReconciliationSeverity.P1)]
    [InlineData(ReconciliationCheck.OrphanedLedgerEntry, ReconciliationSeverity.P1)]
    [InlineData(ReconciliationCheck.ExpiredHoldOutstanding, ReconciliationSeverity.P2)]
    public void Severity_is_decided_by_the_check_not_by_the_caller(
        ReconciliationCheck check,
        ReconciliationSeverity expected)
    {
        // The three that mean a figure is wrong are P1. An expired hold is P2: the money is still
        // accounted for and a release job is simply behind. A P1 that is usually nothing is a P1
        // nobody reads, which would cost us the one alert that matters.
        check.Severity().Should().Be(expected);

        ReconciliationException.Record(
                check, "subject", "detail", Money.Zero, Money.Zero, null, DateTimeOffset.UtcNow)
            .Severity.Should().Be(expected);
    }

    [Fact]
    public void Every_check_has_a_severity()
    {
        // So that adding a check to the enum and forgetting to classify it fails here rather
        // than at 03:00 inside the job.
        foreach (var check in Enum.GetValues<ReconciliationCheck>())
        {
            var act = () => check.Severity();
            act.Should().NotThrow($"{check} needs a severity");
        }
    }

    [Fact]
    public void Seeing_it_again_bumps_the_counter_rather_than_starting_over()
    {
        var exception = Unbalanced();
        var later = DateTimeOffset.UtcNow.AddDays(1);

        exception.SeenAgain(new Money(150_000), new Money(139_000), later);

        exception.TimesSeen.Should().Be(2);
        exception.LastSeenAt.Should().Be(later);

        // The figures are refreshed, because a drift that is growing is a different problem from
        // one that is static and the difference matters to whoever is diagnosing it.
        exception.ActualMinor.AmountMinor.Should().Be(139_000);
        exception.DifferenceMinor.AmountMinor.Should().Be(-11_000);
    }

    [Fact]
    public void A_resolved_exception_that_recurs_is_reopened()
    {
        var exception = Unbalanced();
        exception.Resolve("Corrected by hand in DB-1234.", DateTimeOffset.UtcNow);

        exception.Status.Should().Be(ReconciliationStatus.Resolved);

        // Tonight's run finds it again, so it was not fixed after all.
        exception.SeenAgain(new Money(150_000), new Money(140_000), DateTimeOffset.UtcNow.AddDays(1));

        // Reopened, and the claim that it was fixed is withdrawn. Leaving it resolved would park
        // a live problem in a filtered queue where nobody looks again.
        exception.Status.Should().Be(ReconciliationStatus.Open);
        exception.ResolvedAt.Should().BeNull();
        exception.ResolutionNote.Should().BeNull();
    }

    [Fact]
    public void An_acknowledged_exception_that_recurs_stays_acknowledged()
    {
        var exception = Unbalanced();
        exception.Acknowledge();

        exception.SeenAgain(new Money(150_000), new Money(140_000), DateTimeOffset.UtcNow.AddDays(1));

        // Somebody is already on it. Reopening would only make it look unowned.
        exception.Status.Should().Be(ReconciliationStatus.Acknowledged);
    }

    [Fact]
    public void Resolving_demands_a_note()
    {
        var exception = Unbalanced();

        // "What happened in March" is the whole value of this table, and an empty note loses it.
        var act = () => exception.Resolve("   ", DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
        exception.Status.Should().Be(ReconciliationStatus.Open);
    }

    [Fact]
    public void A_discrepancy_must_say_what_it_is_about()
    {
        // Without a subject there is nothing to deduplicate on, so the job would write a fresh
        // row every night for the same problem.
        var act = () => ReconciliationException.Record(
            ReconciliationCheck.WalletBalanceDrift,
            string.Empty,
            "detail",
            Money.Zero,
            Money.Zero,
            null,
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
