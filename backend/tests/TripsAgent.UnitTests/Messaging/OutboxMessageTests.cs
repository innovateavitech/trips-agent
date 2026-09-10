using FluentAssertions;
using TripsAgent.Domain.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// <see cref="OutboxMessage"/> is the record a business transaction writes so a separate
/// dispatcher can publish it later — see issue #30. These tests cover the entity's own rules;
/// the transactional guarantee ("committed with the state change that produced it") is proved
/// against real PostgreSQL in <c>OutboxAndInboxTests</c>.
/// </summary>
public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_message_should_start_undispatched()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);

        message.IsDispatched.Should().BeFalse();
        message.DispatchedAt.Should().BeNull();
        message.Attempts.Should().Be(0);
        message.LastError.Should().BeNull();
        message.NotBefore.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("   ", "{}")]
    [InlineData("Ns.Event, Asm", null)]
    [InlineData("Ns.Event, Asm", "")]
    [InlineData("Ns.Event, Asm", "   ")]
    public void Create_should_reject_a_blank_type_or_payload(string? messageType, string? payloadJson)
    {
        var act = () => OutboxMessage.Create(messageType!, payloadJson!, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkDispatched_should_record_when()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);

        message.MarkDispatched(Now.AddSeconds(3));

        message.IsDispatched.Should().BeTrue();
        message.DispatchedAt.Should().Be(Now.AddSeconds(3));
    }

    [Fact]
    public void MarkDispatched_should_be_idempotent()
    {
        // A dispatcher that crashes after publishing but before saving will call this again on
        // the next pass, having republished the same message. The first DispatchedAt — when it
        // actually first went out — must survive, not the second call's later timestamp.
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);
        message.MarkDispatched(Now.AddSeconds(1));

        message.MarkDispatched(Now.AddSeconds(99));

        message.DispatchedAt.Should().Be(Now.AddSeconds(1));
    }

    [Fact]
    public void MarkDispatched_should_clear_a_previous_error()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);
        message.RecordFailedAttempt("boom", Now, TimeSpan.FromSeconds(1));

        message.MarkDispatched(Now.AddMinutes(1));

        message.LastError.Should().BeNull();
    }

    [Fact]
    public void RecordFailedAttempt_should_count_and_set_the_backoff()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);

        message.RecordFailedAttempt("connection refused", Now, TimeSpan.FromSeconds(30));

        message.Attempts.Should().Be(1);
        message.LastError.Should().Be("connection refused");
        message.NotBefore.Should().Be(Now.AddSeconds(30));
        message.IsDispatched.Should().BeFalse();
    }

    [Fact]
    public void RecordFailedAttempt_should_accumulate_across_calls()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);

        message.RecordFailedAttempt("first", Now, TimeSpan.FromSeconds(1));
        message.RecordFailedAttempt("second", Now.AddSeconds(1), TimeSpan.FromSeconds(2));

        message.Attempts.Should().Be(2);
        message.LastError.Should().Be("second");
    }

    [Fact]
    public void RecordFailedAttempt_should_truncate_a_very_long_error()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);
        var huge = new string('x', OutboxMessage.MaxErrorLength + 500);

        message.RecordFailedAttempt(huge, Now, TimeSpan.FromSeconds(1));

        message.LastError.Should().HaveLength(OutboxMessage.MaxErrorLength);
    }

    [Fact]
    public void RecordFailedAttempt_should_reject_a_blank_error()
    {
        var message = OutboxMessage.Create("Ns.Event, Asm", "{}", Now);

        var act = () => message.RecordFailedAttempt("   ", Now, TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentException>();
    }
}
