using FluentAssertions;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// <see cref="OutboxBacklogSnapshot.Assess"/> is pure — no database, no clock — so every threshold
/// boundary is covered directly rather than through an integration test.
/// </summary>
public class OutboxBacklogSnapshotTests
{
    private static readonly OutboxOptions Options = new()
    {
        BacklogWarningCount = 500,
        BacklogCriticalCount = 5_000,
        BacklogWarningAge = TimeSpan.FromMinutes(1),
        BacklogCriticalAge = TimeSpan.FromMinutes(10),
    };

    [Fact]
    public void Nothing_pending_or_failed_is_healthy()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 0, FailedCount: 0, OldestPendingAge: TimeSpan.Zero);

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Healthy);
    }

    [Fact]
    public void A_small_pending_count_well_under_the_warning_age_is_healthy()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 3, FailedCount: 0, OldestPendingAge: TimeSpan.FromSeconds(2));

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Healthy);
    }

    [Fact]
    public void Reaching_the_warning_count_is_a_warning()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 500, FailedCount: 0, OldestPendingAge: TimeSpan.Zero);

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Warning);
    }

    [Fact]
    public void Reaching_the_critical_count_is_critical()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 5_000, FailedCount: 0, OldestPendingAge: TimeSpan.Zero);

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Critical);
    }

    [Fact]
    public void A_small_count_that_is_old_is_still_a_warning()
    {
        // Ten pending messages is not a big backlog by count, but if the oldest has waited a
        // minute something is wrong: the Worker or the broker, not the volume.
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 10, FailedCount: 0, OldestPendingAge: TimeSpan.FromMinutes(1));

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Warning);
    }

    [Fact]
    public void A_small_count_that_is_very_old_is_critical()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 1, FailedCount: 0, OldestPendingAge: TimeSpan.FromMinutes(10));

        snapshot.Assess(Options).Level.Should().Be(OutboxBacklogLevel.Critical);
    }

    [Fact]
    public void Any_failed_message_is_at_least_a_warning_even_with_no_backlog()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 0, FailedCount: 1, OldestPendingAge: TimeSpan.Zero);

        var assessment = snapshot.Assess(Options);

        assessment.Level.Should().Be(OutboxBacklogLevel.Warning);
        assessment.Summary.Should().Contain("failed").And.Contain("status = 'failed'");
    }

    [Fact]
    public void The_critical_summary_says_what_to_check()
    {
        var snapshot = new OutboxBacklogSnapshot(PendingCount: 5_000, FailedCount: 0, OldestPendingAge: TimeSpan.Zero);

        snapshot.Assess(Options).Summary.Should().Contain("Worker").And.Contain("broker");
    }
}
