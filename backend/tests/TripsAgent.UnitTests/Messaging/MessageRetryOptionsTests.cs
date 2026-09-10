using FluentAssertions;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// Validation runs at startup, before the first message is ever consumed. A retry schedule nobody
/// intended should stop the container from booting — which someone notices — rather than quietly
/// dead-lettering the first booking of the day.
/// </summary>
public class MessageRetryOptionsTests
{
    [Fact]
    public void The_defaults_should_pass()
    {
        var act = () => new MessageRetryOptions().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void The_defaults_should_retry_a_handful_of_times_with_a_growing_gap()
    {
        // Long enough to ride out a supplier blip, short enough that a genuinely poisonous message
        // reaches the dead-letter queue while someone is still reading the logs.
        var options = new MessageRetryOptions();

        options.RetryLimit.Should().Be(5);
        options.RetryMinInterval.Should().BeLessThan(options.RetryMaxInterval);
        options.RetryIntervalDelta.Should().BePositive();
    }

    [Fact]
    public void Zero_retries_should_be_allowed()
    {
        // Straight to the dead-letter queue on first failure. A legitimate choice for a queue
        // whose work must never be repeated.
        var act = () => new MessageRetryOptions { RetryLimit = 0 }.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void A_negative_retry_limit_should_fail_loudly()
    {
        Throwing(new MessageRetryOptions { RetryLimit = -1 })
            .Message.Should().Contain("Messaging:RetryLimit");
    }

    [Fact]
    public void A_negative_first_delay_should_fail_loudly()
    {
        Throwing(new MessageRetryOptions { RetryMinInterval = TimeSpan.FromSeconds(-1) })
            .Message.Should().Contain("RetryMinInterval");
    }

    [Fact]
    public void A_backoff_that_starts_above_its_ceiling_should_fail_loudly()
    {
        // Easy to get wrong by swapping two lines in appsettings, and MassTransit would otherwise
        // accept it and produce a retry schedule nobody intended.
        var options = new MessageRetryOptions
        {
            RetryMinInterval = TimeSpan.FromMinutes(2),
            RetryMaxInterval = TimeSpan.FromSeconds(30),
        };

        Throwing(options).Message.Should().Contain("nowhere to grow into");
    }

    private static InvalidOperationException Throwing(MessageRetryOptions options)
    {
        var act = () => options.Validate();

        return act.Should().Throw<InvalidOperationException>().Which;
    }
}
