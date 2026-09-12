namespace TripsAgent.Domain.Billing;

/// <summary>
/// When to try a failed subscription charge again, and when to stop trying.
/// </summary>
/// <remarks>
/// <para>
/// Days 1, 3, 5 and 7 after the first failure — issue 65. Four attempts over a week, spaced so the
/// commonest causes have time to fix themselves: a card that hit its daily limit clears overnight,
/// an expired card takes a few days for the agency to replace, and a bank outage is over well
/// inside a week.
/// </para>
/// <para>
/// This is a pure calculation with no database and no clock of its own, so the whole schedule —
/// including what happens after the last attempt — is testable without either.
/// </para>
/// </remarks>
public static class DunningSchedule
{
    /// <summary>
    /// Days after the first failure on which to try again. Attempt 1 happens on day 1, attempt 4 on
    /// day 7; there is no attempt 5.
    /// </summary>
    public static readonly IReadOnlyList<int> RetryDays = [1, 3, 5, 7];

    /// <summary>How many times a failed charge is retried before the subscription is acted on.</summary>
    public static int MaxRetries => RetryDays.Count;

    /// <summary>
    /// When to make retry number <paramref name="retriesSoFar"/> + 1, counting from
    /// <paramref name="firstFailedAt"/>. Null when the schedule is exhausted.
    /// </summary>
    /// <param name="firstFailedAt">When the charge first failed. Every retry is measured from here.</param>
    /// <param name="retriesSoFar">
    /// How many retries have already been made — zero right after the first failure.
    /// </param>
    /// <remarks>
    /// Measured from the first failure rather than from the previous attempt, so a day the job did
    /// not run cannot stretch a seven-day schedule into a fortnight and leave an unpaid agency
    /// trading for twice as long as anyone agreed.
    /// </remarks>
    public static DateTimeOffset? NextAttemptAt(DateTimeOffset firstFailedAt, int retriesSoFar)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retriesSoFar);

        return retriesSoFar >= RetryDays.Count ? null : firstFailedAt.AddDays(RetryDays[retriesSoFar]);
    }

    /// <summary>True when <paramref name="retriesSoFar"/> retries means there is nothing left to try.</summary>
    public static bool IsExhausted(int retriesSoFar) => retriesSoFar >= RetryDays.Count;

    /// <summary>
    /// The whole schedule from one first failure, for a screen that has to say "we will try again
    /// on…" and for a test that has to assert the shape of it.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> AllAttemptsFrom(DateTimeOffset firstFailedAt) =>
        [.. RetryDays.Select(days => firstFailedAt.AddDays(days))];
}

/// <summary>What happens to a subscription when dunning runs out.</summary>
/// <remarks>
/// Two outcomes, and which one applies depends on whether the platform has a fallback tier
/// configured. Falling back to a free plan is much better than suspension — the agency keeps its
/// bookings, its customers and its storefront, and simply loses what it stopped paying for. There
/// is only ever a suspension when there is no free plan to fall back to.
/// </remarks>
public enum DunningOutcome
{
    /// <summary>Moved to the fallback tier. The account keeps working with the free tier's entitlements.</summary>
    DowngradedToFallback = 1,

    /// <summary>No fallback tier exists, so the agency is suspended — build-plan decision 14.</summary>
    Suspended = 2,
}
