using Hangfire;
using TripsAgent.Application.Payments;

namespace TripsAgent.Infrastructure.Payments;

/// <summary>
/// Sends the payouts Finance has approved.
/// </summary>
/// <remarks>
/// <para>
/// Every five minutes, so an approval turns into money within a coffee break rather than
/// overnight. Approving is the slow, human part; once it is done there is nothing left to wait
/// for.
/// </para>
/// <para>
/// Safe to run twice, and it will be — a redeploy re-registers it and an operator may trigger it
/// by hand. Only an <c>Approved</c> payout is ever picked up, and sending moves it out of that
/// state before the gateway is called, so two overlapping runs cannot both send one payout.
/// </para>
/// <para>
/// Registered by the Worker only. Every API instance running this would be every API instance
/// sending transfers.
/// </para>
/// </remarks>
public static class PayoutSenderSchedule
{
    public const string JobId = "payout-sender";

    public const string CronExpression = "*/5 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IPayoutTransferService>(
            JobId,
            sender => sender.SendApprovedAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>
/// Asks the gateway what became of every payout in flight.
/// </summary>
/// <remarks>
/// <para>
/// Every fifteen minutes. This is the only way a payout whose transfer call timed out ever
/// reaches a final state — the transfer is never sent again, so if this job stops running the
/// money stays parked in payout-payable and nobody is told.
/// </para>
/// <para>
/// See docs/adr/0008-never-retry-payout-transfers.md. The poller exists because the retry does
/// not.
/// </para>
/// </remarks>
public static class PayoutStatusPollSchedule
{
    public const string JobId = "payout-status-poller";

    public const string CronExpression = "*/15 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IPayoutStatusPoller>(
            JobId,
            poller => poller.PollAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>
/// Chases dispute evidence: reminds an agency as its deadline nears, and records a missed one.
/// </summary>
/// <remarks>
/// Hourly, because a deadline is an instant and a daily job could miss one by 23 hours. A dispute
/// nobody answers is lost by default, so this is the job that stops one sitting in a table until
/// the bank decides without us.
/// </remarks>
public static class DisputeDeadlineSchedule
{
    public const string JobId = "dispute-deadline-monitor";

    public const string CronExpression = "10 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IDisputeDeadlineMonitor>(
            JobId,
            monitor => monitor.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>
/// Reconciles yesterday's gateway settlements against the ledger, every morning.
/// </summary>
/// <remarks>
/// 05:00 UTC, 06:00 in Lagos: after the gateway's overnight settlement, before Finance starts work,
/// and clear of the 02:30 ledger audit. Idempotent — a redeploy or a hand-triggered re-run updates
/// the day's run in place and raises nothing twice.
/// </remarks>
public static class GatewayReconciliationSchedule
{
    public const string JobId = "gateway-reconciliation";

    public const string CronExpression = "0 5 * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IGatewayReconciliation>(
            JobId,
            reconciliation => reconciliation.RunYesterdayAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
