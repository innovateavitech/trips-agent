using Hangfire;
using TripsAgent.Application.Payments;

namespace TripsAgent.Infrastructure.Payments;

/// <summary>
/// Hands a recorded webhook to Hangfire, for the Worker to process.
/// </summary>
/// <remarks>
/// The API enqueues and the Worker executes — the API never runs a Hangfire server, or every API
/// instance would race to process the same event.
/// </remarks>
public sealed class HangfireWebhookDispatcher : IWebhookDispatcher
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireWebhookDispatcher(IBackgroundJobClient jobs) => _jobs = jobs;

    public Task EnqueueAsync(Guid webhookEventId, CancellationToken cancellationToken = default)
    {
        // Hangfire substitutes its own token for CancellationToken.None when the job runs, so a
        // Worker shutting down can still stop it cleanly.
        _jobs.Enqueue<IPaymentWebhookProcessor>(
            processor => processor.ProcessAsync(webhookEventId, CancellationToken.None));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Drains pending webhook events on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The enqueue in <see cref="HangfireWebhookDispatcher"/> is the fast path; this is the one
/// that makes the guarantee. An enqueue can be lost — the process can die between the insert and
/// the enqueue — and a payment nobody credited is the worst failure this module has.
/// </para>
/// <para>
/// Every two minutes, because the agent's own browser redirect already verifies immediately. The
/// webhook path is the backstop for agents who close the tab, so minutes are fine.
/// </para>
/// </remarks>
public static class PaymentWebhookDrainSchedule
{
    public const string JobId = "payment-webhook-drain";

    public const string CronExpression = "*/2 * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<IPaymentWebhookProcessor>(
            JobId,
            processor => processor.DrainAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
