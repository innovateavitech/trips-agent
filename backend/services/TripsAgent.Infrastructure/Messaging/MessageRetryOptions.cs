namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// How hard to retry a failed message before giving up on it and dead-lettering it.
///
/// Bound from the <c>Messaging</c> section of configuration. The broker <em>address</em> is not
/// here — it is a connection string, <c>ConnectionStrings:RabbitMq</c>, exactly like PostgreSQL's.
/// Keeping credentials in a connection string rather than in a settings block is what lets
/// <c>.env</c> supply them the same way it supplies everything else.
/// </summary>
public sealed class MessageRetryOptions
{
    /// <summary>Configuration section these settings are read from.</summary>
    public const string SectionName = "Messaging";

    /// <summary>
    /// How many times a failed message is retried before it is dead-lettered. Five attempts over
    /// roughly a minute is long enough to ride out a supplier blip and short enough that a truly
    /// broken message reaches the dead-letter queue while someone is still looking at the logs.
    /// </summary>
    public int RetryLimit { get; set; } = 5;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan RetryMinInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the delay between retries, however many have failed.</summary>
    public TimeSpan RetryMaxInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How much each successive retry adds to the delay.</summary>
    public TimeSpan RetryIntervalDelta { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fails fast, with an explanation, rather than letting the app start with a retry schedule
    /// nobody intended.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the settings could not work.</exception>
    public void Validate()
    {
        if (RetryLimit < 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryLimit is {RetryLimit}. Use 0 for no retries, never a negative number.");
        }

        if (RetryMinInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryMinInterval is {RetryMinInterval}, which is a negative delay.");
        }

        if (RetryMinInterval > RetryMaxInterval)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryMinInterval ({RetryMinInterval}) is longer than RetryMaxInterval "
                + $"({RetryMaxInterval}), so the backoff has nowhere to grow into. Swap them.");
        }
    }
}
