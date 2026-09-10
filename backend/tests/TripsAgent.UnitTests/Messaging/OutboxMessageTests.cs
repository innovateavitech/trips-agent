using FluentAssertions;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record ProbeEvent(string Detail);

    [Fact]
    public void Create_serialises_the_message_by_its_runtime_type()
    {
        // Passed as `object`, matching how AppDbContext hands it an IDomainEvent — an interface
        // with no properties of its own. Serialising through the static type would write "{}".
        object message = new ProbeEvent("hello");

        var outboxMessage = OutboxMessage.Create(message, agencyId: null, Now);

        outboxMessage.Payload.Should().Contain("hello");
        outboxMessage.MessageType.Should().Contain(nameof(ProbeEvent)).And.Contain("TripsAgent.UnitTests");
    }

    [Fact]
    public void Create_starts_pending_and_due_immediately()
    {
        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, Now);

        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.NextAttemptAt.Should().Be(Now);
        message.AttemptCount.Should().Be(0);
        message.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public void Create_normalises_a_non_UTC_occurred_at_to_UTC()
    {
        var lagos = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(1));

        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, lagos);

        message.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
        message.OccurredAt.Should().Be(lagos.ToUniversalTime());
    }

    [Fact]
    public void Create_carries_the_agency_id_through()
    {
        var agencyId = Guid.CreateVersion7();

        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId, Now);

        message.AgencyId.Should().Be(agencyId);
    }

    [Fact]
    public void MarkDispatched_moves_to_dispatched_and_clears_any_earlier_error()
    {
        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, Now);
        message.RecordFailure("first attempt failed", retryAt: Now.AddSeconds(5));

        message.MarkDispatched(Now.AddSeconds(6));

        message.Status.Should().Be(OutboxMessageStatus.Dispatched);
        message.DispatchedAt.Should().Be(Now.AddSeconds(6));
        message.LastError.Should().BeNull();
        message.AttemptCount.Should().Be(2, "the failed attempt and the one that succeeded both count");
    }

    [Fact]
    public void RecordFailure_with_a_retry_time_stays_pending_and_reschedules()
    {
        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, Now);
        var retryAt = Now.AddSeconds(5);

        message.RecordFailure("boom", retryAt);

        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.NextAttemptAt.Should().Be(retryAt);
        message.AttemptCount.Should().Be(1);
        message.LastError.Should().Be("boom");
    }

    [Fact]
    public void RecordFailure_with_no_retry_time_gives_up()
    {
        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, Now);

        message.RecordFailure("boom", retryAt: null);

        message.Status.Should().Be(OutboxMessageStatus.Failed);
    }

    [Fact]
    public void RecordFailure_trims_a_very_long_error_rather_than_storing_it_unbounded()
    {
        var message = OutboxMessage.Create(new ProbeEvent("x"), agencyId: null, Now);
        var longError = new string('x', OutboxMessage.MaxErrorLength + 500);

        message.RecordFailure(longError, retryAt: Now.AddSeconds(1));

        message.LastError.Should().HaveLength(OutboxMessage.MaxErrorLength);
    }

    [Fact]
    public void TypeNameOf_carries_no_assembly_version_so_a_new_build_can_still_read_old_messages()
    {
        var name = OutboxMessage.TypeNameOf(typeof(ProbeEvent));

        name.Should().NotContain("Version=");
        name.Should().Contain(nameof(ProbeEvent));

        // And it has to actually resolve — this is the contract MassTransitOutboxPublisher relies on.
        Type.GetType(name, throwOnError: false).Should().Be<ProbeEvent>();
    }
}
