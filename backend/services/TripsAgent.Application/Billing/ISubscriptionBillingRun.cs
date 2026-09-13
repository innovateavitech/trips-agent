namespace TripsAgent.Application.Billing;

/// <summary>
/// The daily pass over every subscription: apply what was scheduled, charge what is due, and chase
/// what failed.
/// </summary>
/// <remarks>
/// <para>
/// Runs as the <c>subscription-billing</c> recurring job in the Worker. Safe to run twice and safe
/// to trigger by hand from the Hangfire dashboard, which matters because the first thing anyone does
/// when a renewal looks wrong is run it again: each charge attempt carries a reference unique to
/// that attempt, and the gateway refuses a reference it has already charged.
/// </para>
/// <para>
/// It is deliberately one job rather than four. The four things it does are ordered — a migration
/// that lands today changes what today's renewal charges — and four jobs on four schedules would
/// make that ordering a matter of luck.
/// </para>
/// </remarks>
public interface ISubscriptionBillingRun
{
    public Task<SubscriptionBillingRunResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one pass did.</summary>
/// <param name="RunId">Also the correlation id on every audit row the run wrote.</param>
/// <param name="MigrationsApplied">Scheduled tier changes that came due and landed.</param>
/// <param name="TrialsEnded">Trials that ran out and were invoiced for their first period.</param>
/// <param name="Renewed">Subscriptions whose period ran out and which were invoiced again.</param>
/// <param name="Charged">Charges that went through, across renewals and dunning retries.</param>
/// <param name="Failed">Charges that were declined.</param>
/// <param name="Downgraded">Agencies moved to the fallback plan because dunning ran out.</param>
/// <param name="Suspended">Agencies suspended because dunning ran out and there is no fallback plan.</param>
/// <param name="Errors">One line per agency the run could not finish, so the rest still ran.</param>
public sealed record SubscriptionBillingRunResult(
    Guid RunId,
    int MigrationsApplied,
    int TrialsEnded,
    int Renewed,
    int Charged,
    int Failed,
    int Downgraded,
    int Suspended,
    IReadOnlyList<string> Errors)
{
    /// <summary>Nothing was due and nothing went wrong.</summary>
    public bool WasQuiet =>
        MigrationsApplied == 0 && TrialsEnded == 0 && Renewed == 0 && Charged == 0
        && Failed == 0 && Downgraded == 0 && Suspended == 0 && Errors.Count == 0;
}
