using FluentAssertions;
using TripsAgent.Domain.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// <see cref="InboxMessage"/> is a record that one consumer has already handled one message —
/// the dedupe half of the outbox pattern. See issue #30.
/// </summary>
public class InboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_should_set_every_field()
    {
        var messageId = Guid.CreateVersion7();

        var record = InboxMessage.Create(messageId, "IssueTicketConsumer", Now);

        record.MessageId.Should().Be(messageId);
        record.ConsumerName.Should().Be("IssueTicketConsumer");
        record.ProcessedAt.Should().Be(Now);
    }

    [Fact]
    public void Create_should_reject_an_empty_message_id()
    {
        var act = () => InboxMessage.Create(Guid.Empty, "SomeConsumer", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_should_reject_a_blank_consumer_name(string? consumerName)
    {
        var act = () => InboxMessage.Create(Guid.CreateVersion7(), consumerName!, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_should_reject_a_consumer_name_longer_than_the_column()
    {
        var tooLong = new string('a', InboxMessage.MaxConsumerNameLength + 1);

        var act = () => InboxMessage.Create(Guid.CreateVersion7(), tooLong, Now);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain(InboxMessage.MaxConsumerNameLength.ToString());
    }

    [Fact]
    public void Create_should_accept_a_consumer_name_exactly_at_the_limit()
    {
        var exact = new string('a', InboxMessage.MaxConsumerNameLength);

        var act = () => InboxMessage.Create(Guid.CreateVersion7(), exact, Now);

        act.Should().NotThrow();
    }
}
