namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// How the outbox dispatcher behaves. Bound from the <c>Outbox</c> section of configuration.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section these settings are read from.</summary>
    public const string SectionName = "Outbox";

    /// <summary>
    /// How often the dispatcher polls for pending messages. Plan §3 asks for every 1-5 seconds;
    /// two is the middle of that band.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many pending messages one dispatch pass publishes at most.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Failed attempts before a message is left alone rather than retried forever. It still sits
    /// in the table — nothing is deleted — so an operator can see it and decide what to do; the
    /// dispatcher just stops spending a slot on it every tick.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// How many messages still waiting to be dispatched triggers a warning log. The AC asks for
    /// backlog depth to be "monitored and alerts" — this is that, until the platform has a real
    /// metrics or paging pipeline to wire it into. See <c>OutboxDispatcherHostedService</c>.
    /// </summary>
    public int BacklogAlertThreshold { get; set; } = 500;

    /// <summary>
    /// Fails fast, with an explanation, rather than letting the app start with settings nobody
    /// intended.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the settings could not work.</exception>
    public void Validate()
    {
        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:PollingInterval is {PollingInterval}. It must be a positive duration — " +
                "zero or negative would spin the dispatcher in a tight loop against the database.");
        }

        if (BatchSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}:BatchSize is {BatchSize}. Use at least 1.");
        }

        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException($"{SectionName}:MaxAttempts is {MaxAttempts}. Use at least 1.");
        }

        if (BacklogAlertThreshold < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BacklogAlertThreshold is {BacklogAlertThreshold}. Use at least 1.");
        }
    }
}
