using MassTransit;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.Infrastructure.Notifications;

// ============================================================================================
//  The traveller's side of the booking pipeline's events (#42–#46). Each consumer acts as the
//  event's agency, as the pipeline's own consumers do, and hands the work to BookingFollowUps.
//  All three are safe to redeliver: every email they stage is keyed, and a document is issued
//  once. BookingTicketed is not here — it is the pipeline's own step, which captures the payment.
// ============================================================================================

/// <summary>
/// Everything a confirmed booking sets off: the traveller's invoice, vouchers and "booking
/// confirmed" email, and the customer record the booking belongs to (#62). Bound to
/// <c>documents.render</c> in <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, one consumer, because one message type has exactly one consumer here — published
/// messages fan out, so a second consumer of <see cref="BookingConfirmed"/> would be a second copy
/// of the event and a second set of everything it causes. <c>ConsumerRoutingTests</c> holds that
/// line.
/// </para>
/// <para>
/// The traveller's side runs first: it is what someone is waiting for. Both halves are safe to run
/// again, so a redelivery after either one finds its work already done — the emails are keyed, the
/// documents are issued once, and an order already linked to a customer stays linked.
/// </para>
/// </remarks>
public sealed class BookingConfirmedConsumer(
    TenantContext tenant,
    AuditContext audit,
    BookingFollowUps followUps,
    CustomerBookingRecorder customers) : IConsumer<BookingConfirmed>
{
    public async Task Consume(ConsumeContext<BookingConfirmed> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActingAs.Agency(tenant, audit, context.Message.AgencyId, context);

        await followUps.ConfirmedAsync(context.Message, context.CancellationToken);
        await customers.RecordAsync(context.Message, context.CancellationToken);
    }
}

/// <summary>
/// "One item in your booking needs attention", for a line waiting in the agent's resolution queue
/// (#44). Bound to <c>notifications.email</c>.
/// </summary>
public sealed class BookingNeedsResolutionConsumer(
    TenantContext tenant,
    AuditContext audit,
    BookingFollowUps followUps) : IConsumer<BookingNeedsResolution>
{
    public Task Consume(ConsumeContext<BookingNeedsResolution> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActingAs.Agency(tenant, audit, context.Message.AgencyId, context);
        return followUps.NeedsResolutionAsync(context.Message, context.CancellationToken);
    }
}

/// <summary>
/// The traveller's refund notice after a reversal (#43) or the agent's refund (#44). Bound to
/// <c>notifications.email</c>.
/// </summary>
public sealed class PaymentReversedConsumer(
    TenantContext tenant,
    AuditContext audit,
    BookingFollowUps followUps) : IConsumer<PaymentReversed>
{
    public Task Consume(ConsumeContext<PaymentReversed> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActingAs.Agency(tenant, audit, context.Message.AgencyId, context);
        return followUps.PaymentReversedAsync(context.Message, context.CancellationToken);
    }
}

/// <summary>What each consumer here records before it works: the event's agency as tenant, the system as actor.</summary>
internal static class ActingAs
{
    public static void Agency(TenantContext tenant, AuditContext audit, Guid agencyId, ConsumeContext context)
    {
        tenant.SetTenant(agencyId);
        audit.ActorType = AuditActorType.System;
        audit.AgencyId = agencyId;
        audit.CorrelationId = context.CorrelationId?.ToString();
    }
}
