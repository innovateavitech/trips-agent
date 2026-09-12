using Hangfire;
using MassTransit;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>
/// Issues the ticket an <see cref="IssueSupplierTicket"/> asks for. Bound to <c>booking.saga</c> in
/// <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>
/// <b>Redelivery is safe, and so is the queue's retry.</b> RabbitMQ delivers at least once, and the
/// endpoint retries a consumer that throws. Neither can issue a second ticket: a second pass finds the
/// booking no longer <c>PriceConfirmed</c> and sends nothing (<see cref="TicketIssuanceService"/>). What is
/// never retried is the supplier call itself — ADR-0003.
/// </remarks>
public sealed class IssueSupplierTicketConsumer(
    TenantContext tenant,
    AuditContext audit,
    TicketIssuanceService issuance) : IConsumer<IssueSupplierTicket>
{
    public async Task Consume(ConsumeContext<IssueSupplierTicket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        // A message has no request behind it, so it names its own agency: the tenant for this scope.
        tenant.SetTenant(message.AgencyId);
        audit.ActorType = AuditActorType.System;
        audit.AgencyId = message.AgencyId;
        audit.CorrelationId = message.CorrelationId ?? context.CorrelationId?.ToString();

        await issuance.IssueAsync(message.OrderLineId, message.IdempotencyKey, audit.CorrelationId, context.CancellationToken);
    }
}

/// <summary>
/// The status poller, as Hangfire runs it (#37). Not retried: the next run is thirty seconds away.
/// </summary>
/// <remarks>
/// No <c>DisableConcurrentExecution</c>: runs may overlap, on one Worker or many, because each claims its
/// own bookings with SKIP LOCKED. A run that outlasts its interval costs nothing.
/// </remarks>
public sealed class SupplierBookingStatusPollJob(SupplierBookingStatusPoller poller, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return poller.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the status poller on Hangfire's clock: every thirty seconds.</summary>
public static class SupplierBookingStatusPollSchedule
{
    public const string JobId = "supplier-booking-status-poll";

    /// <summary>Six fields, so Hangfire reads the first as seconds: at :00 and :30 of every minute.</summary>
    public const string CronExpression = "*/30 * * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<SupplierBookingStatusPollJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

/// <summary>The ticket time limit monitor, as Hangfire runs it (#38). Not retried: the next run is a minute away.</summary>
public sealed class TicketTimeLimitMonitorJob(TicketTimeLimitMonitor monitor, AuditContext audit)
{
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task<TicketTimeLimitRun> RunAsync(CancellationToken cancellationToken)
    {
        audit.ActorType = AuditActorType.System;
        return monitor.RunAsync(cancellationToken);
    }
}

/// <summary>Puts the ticket time limit monitor on Hangfire's clock: every minute.</summary>
public static class TicketTimeLimitMonitorSchedule
{
    public const string JobId = "ticket-time-limit-monitor";

    public const string CronExpression = "* * * * *";

    public static void Register(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<TicketTimeLimitMonitorJob>(
            JobId,
            job => job.RunAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
