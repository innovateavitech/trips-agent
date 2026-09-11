using Hangfire;
using MassTransit;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.Infrastructure.Checkout;

/// <summary>
/// Turns a ticket into a confirmed, paid-for booking (#42). Bound to <c>booking.saga</c> in
/// <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>Redelivery is harmless: <see cref="CheckoutCompletion"/> does its work once per line.</remarks>
public sealed class BookingTicketedConsumer(
    TenantContext tenant,
    AuditContext audit,
    CheckoutCompletion completion) : IConsumer<BookingTicketed>
{
    public async Task Consume(ConsumeContext<BookingTicketed> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        tenant.SetTenant(message.AgencyId);
        audit.ActorType = AuditActorType.System;
        audit.AgencyId = message.AgencyId;
        audit.CorrelationId = context.CorrelationId?.ToString();

        await completion.CompleteAsync(message, context.CancellationToken);
    }
}

/// <summary>
/// Gives the money back when the supplier's reversal rules say so (#43). Bound to
/// <c>payments.reversal</c>, whose endpoint retries with a growing back-off.
/// </summary>
/// <remarks>
/// <b>Escalates on the last attempt.</b> After the endpoint's final retry the message is dead-lettered,
/// and from then on only a person can move the money. So a failure on that last attempt raises a P1
/// before it lets go — with the change tracker cleared first, so the failed attempt's half-made changes
/// can never be saved by the alert's own write.
/// </remarks>
public sealed class PaymentReversalRequiredConsumer(
    TenantContext tenant,
    AuditContext audit,
    PaymentReversalService reversals,
    IAppDbContext db,
    IPlatformAlerter alerter,
    MessageRetryOptions retry) : IConsumer<PaymentReversalRequired>
{
    public async Task Consume(ConsumeContext<PaymentReversalRequired> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        tenant.SetTenant(message.AgencyId);
        audit.ActorType = AuditActorType.System;
        audit.AgencyId = message.AgencyId;
        audit.CorrelationId = context.CorrelationId?.ToString();

        try
        {
            await reversals.ReverseAsync(message, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && context.GetRetryAttempt() >= retry.RetryLimit)
        {
            db.ChangeTracker.Clear();

            await alerter.RaiseAsync(
                new PlatformAlert(
                    AlertSeverity.P1,
                    "A payment reversal keeps failing",
                    $"Reversing supplier booking {message.SupplierBookingId} (order line {message.OrderLineId}, supplier status "
                    + $"{message.SupplierStatusCode}, evidence poll {message.SupplierStatusPollId}) failed {retry.RetryLimit + 1} times; "
                    + $"the last error was {ex.GetType().Name}: {ex.Message}. The message is now in payments.reversal_error and "
                    + "nothing has been refunded. Fix the cause, then move it back to payments.reversal.",
                    PaymentReversalService.AlertSource,
                    message.AgencyId),
                CancellationToken.None);

            throw;
        }
    }
}

/// <summary>The checkout sweeper, as Hangfire runs it (#42). Not retried: the next run is a minute away.</summary>
public sealed class CheckoutSweepJob(CheckoutSweeper sweeper, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return sweeper.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the checkout sweeper on Hangfire's clock: every minute.</summary>
public static class CheckoutSweepSchedule
{
    public const string JobId = "checkout-sweep";

    public const string CronExpression = "* * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<CheckoutSweepJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
