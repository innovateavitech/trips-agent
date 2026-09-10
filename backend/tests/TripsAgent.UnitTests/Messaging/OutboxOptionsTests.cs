using FluentAssertions;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.UnitTests.Messaging;

/// <summary>
/// The defaults are exercised by every other outbox test that never overrides them; these tests
/// cover only the validation and the retry-delay maths, which nothing else touches.
/// </summary>
public class OutboxOptionsTests
{
    [Fact]
    public void Defaults_pass_validation() =>
        FluentActions.Invoking(() => new OutboxOptions().Validate()).Should().NotThrow();

    [Theory]
    [InlineData(0)] // below the 1s floor
    [InlineData(6)] // above the 5s ceiling
    public void PollInterval_outside_one_to_five_seconds_is_rejected(int seconds)
    {
        var options = new OutboxOptions { PollInterval = TimeSpan.FromSeconds(seconds) };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*PollInterval*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void BatchSize_outside_one_to_a_thousand_is_rejected(int batchSize)
    {
        var options = new OutboxOptions { BatchSize = batchSize };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*BatchSize*");
    }

    [Fact]
    public void MaxAttempts_below_one_is_rejected()
    {
        var options = new OutboxOptions { MaxAttempts = 0 };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxAttempts*");
    }

    [Fact]
    public void RetryBaseDelay_longer_than_RetryMaxDelay_is_rejected()
    {
        var options = new OutboxOptions
        {
            RetryBaseDelay = TimeSpan.FromMinutes(20),
            RetryMaxDelay = TimeSpan.FromMinutes(10),
        };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*RetryBaseDelay*");
    }

    [Fact]
    public void RetryBaseDelay_of_zero_is_rejected()
    {
        var options = new OutboxOptions { RetryBaseDelay = TimeSpan.Zero };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*RetryBaseDelay*");
    }

    [Fact]
    public void BacklogWarningCount_above_BacklogCriticalCount_is_rejected()
    {
        var options = new OutboxOptions { BacklogWarningCount = 10_000, BacklogCriticalCount = 5_000 };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*BacklogWarningCount*");
    }

    [Fact]
    public void BacklogWarningAge_above_BacklogCriticalAge_is_rejected()
    {
        var options = new OutboxOptions
        {
            BacklogWarningAge = TimeSpan.FromHours(1),
            BacklogCriticalAge = TimeSpan.FromMinutes(10),
        };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*BacklogWarningAge*");
    }

    [Fact]
    public void BacklogCheckInterval_of_zero_is_rejected()
    {
        var options = new OutboxOptions { BacklogCheckInterval = TimeSpan.Zero };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*BacklogCheckInterval*");
    }

    [Fact]
    public void PublishTimeout_of_zero_is_rejected()
    {
        var options = new OutboxOptions { PublishTimeout = TimeSpan.Zero };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*PublishTimeout*");
    }

    [Fact]
    public void The_validation_message_says_how_to_fix_it()
    {
        var options = new OutboxOptions { MaxAttempts = 0 };

        FluentActions.Invoking(options.Validate).Should().Throw<InvalidOperationException>()
            .WithMessage("*Outbox__MaxAttempts*");
    }

    [Fact]
    public void RetryDelayAfter_doubles_with_each_failed_attempt()
    {
        var options = new OutboxOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(5),
            RetryMaxDelay = TimeSpan.FromMinutes(10),
        };

        options.RetryDelayAfter(1).Should().Be(TimeSpan.FromSeconds(5));
        options.RetryDelayAfter(2).Should().Be(TimeSpan.FromSeconds(10));
        options.RetryDelayAfter(3).Should().Be(TimeSpan.FromSeconds(20));
        options.RetryDelayAfter(4).Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void RetryDelayAfter_never_exceeds_the_configured_ceiling()
    {
        var options = new OutboxOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(5),
            RetryMaxDelay = TimeSpan.FromMinutes(10),
        };

        options.RetryDelayAfter(50).Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void RetryDelayAfter_rejects_a_non_positive_attempt_count()
    {
        var options = new OutboxOptions();

        FluentActions.Invoking(() => options.RetryDelayAfter(0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
