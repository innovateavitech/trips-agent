using FluentAssertions;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// Validation runs at startup, matching <c>MessageRetryOptionsTests</c>: settings nobody intended
/// should stop the Worker booting, not surface later as a dispatcher spinning against the
/// database or silently never retrying a failed message.
/// </summary>
public class OutboxOptionsTests
{
    [Fact]
    public void The_defaults_should_pass()
    {
        var act = () => new OutboxOptions().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void The_default_polling_interval_should_sit_inside_the_plans_one_to_five_second_band()
    {
        var interval = new OutboxOptions().PollingInterval;

        interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        interval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_polling_interval_should_be_rejected(int seconds)
    {
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromSeconds(seconds) };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("PollingInterval");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_batch_size_should_be_rejected(int batchSize)
    {
        var options = new OutboxOptions { BatchSize = batchSize };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("BatchSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_max_attempts_should_be_rejected(int maxAttempts)
    {
        var options = new OutboxOptions { MaxAttempts = maxAttempts };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("MaxAttempts");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_backlog_alert_threshold_should_be_rejected(int threshold)
    {
        var options = new OutboxOptions { BacklogAlertThreshold = threshold };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("BacklogAlertThreshold");
    }
}
