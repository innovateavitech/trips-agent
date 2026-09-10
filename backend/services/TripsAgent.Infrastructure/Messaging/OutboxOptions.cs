namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// How often the outbox is drained, how hard a failed publish is retried, and when a growing
/// backlog becomes worth alerting on. Bound from the <c>Outbox</c> configuration section; every
/// setting has a default that works as-is.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section these settings are read from.</summary>
    public const string SectionName = "Outbox";

    /// <summary>Fastest allowed poll. Any faster just hammers the database.</summary>
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Slowest allowed poll. Any slower and a traveller waits noticeably for a confirmation.</summary>
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long the dispatcher waits between looks at the outbox. Must be 1–5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Most messages claimed per transaction. A full batch is followed straight away by another, so
    /// this bounds how long row locks are held, not how fast a backlog drains.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Longest a single publish may take before it counts as failed. The dispatcher holds a row lock
    /// while it waits, so a broker that has gone quiet must not be waited on forever.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Publish attempts before a message is marked failed and left for a person.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Wait after the first failed publish. Doubles with each further failure.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the wait between attempts, however many have failed.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Pending messages at which the backlog is reported as a warning.</summary>
    public int BacklogWarningCount { get; set; } = 500;

    /// <summary>Pending messages at which the backlog is reported as critical.</summary>
    public int BacklogCriticalCount { get; set; } = 5_000;

    /// <summary>Age of the oldest pending message at which the backlog is a warning.</summary>
    public TimeSpan BacklogWarningAge { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Age of the oldest pending message at which the backlog is critical.</summary>
    public TimeSpan BacklogCriticalAge { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the Worker measures the backlog and logs if it is unhealthy.</summary>
    public TimeSpan BacklogCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait after <paramref name="failedAttempts"/> failures before trying again.</summary>
    public TimeSpan RetryDelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);

        // 5s, 10s, 20s, 40s … up to the cap. The exponent is clamped first so a message that has
        // failed a thousand times cannot overflow on its way to hitting the cap anyway.
        var factor = Math.Pow(2, Math.Min(failedAttempts - 1, 30));
        var ticks = Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks);

        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Fails at startup, with an explanation, rather than letting the Worker run with a dispatcher
    /// that never fires or an alert that can never trigger.
    /// </summary>
    /// <exception cref="InvalidOperationException">If a setting could not work.</exception>
    public void Validate()
    {
        if (PollInterval < MinimumPollInterval || PollInterval > MaximumPollInterval)
        {
            throw Invalid(
                nameof(PollInterval),
                $"is {PollInterval}. It must be between {MinimumPollInterval} and {MaximumPollInterval}: "
                + "faster hammers the database, slower leaves travellers waiting for confirmations.");
        }

        if (BatchSize is < 1 or > 1000)
        {
            throw Invalid(nameof(BatchSize), $"is {BatchSize}. Use a number from 1 to 1000.");
        }

        if (PublishTimeout <= TimeSpan.Zero)
        {
            throw Invalid(nameof(PublishTimeout), $"is {PublishTimeout}. It must be above zero.");
        }

        if (MaxAttempts < 1)
        {
            throw Invalid(nameof(MaxAttempts), $"is {MaxAttempts}. A message needs at least one attempt.");
        }

        if (RetryBaseDelay <= TimeSpan.Zero || RetryBaseDelay > RetryMaxDelay)
        {
            throw Invalid(
                nameof(RetryBaseDelay),
                $"is {RetryBaseDelay}. It must be above zero and no longer than RetryMaxDelay ({RetryMaxDelay}).");
        }

        if (BacklogWarningCount < 1 || BacklogWarningCount > BacklogCriticalCount)
        {
            throw Invalid(
                nameof(BacklogWarningCount),
                $"is {BacklogWarningCount}. It must be at least 1 and no more than BacklogCriticalCount ({BacklogCriticalCount}).");
        }

        if (BacklogWarningAge <= TimeSpan.Zero || BacklogWarningAge > BacklogCriticalAge)
        {
            throw Invalid(
                nameof(BacklogWarningAge),
                $"is {BacklogWarningAge}. It must be above zero and no longer than BacklogCriticalAge ({BacklogCriticalAge}).");
        }

        if (BacklogCheckInterval <= TimeSpan.Zero)
        {
            throw Invalid(nameof(BacklogCheckInterval), $"is {BacklogCheckInterval}. It must be above zero.");
        }
    }

    private static InvalidOperationException Invalid(string setting, string problem) =>
        new($"""
             Outbox:{setting} {problem}

             Set it with the environment variable Outbox__{setting}, or under "Outbox" in
             appsettings.json. Leaving it out uses the default, which is almost always right.
             """);
}
