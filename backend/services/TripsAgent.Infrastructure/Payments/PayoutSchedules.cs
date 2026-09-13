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
