using MassTransit;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Notifications;

namespace TripsAgent.Infrastructure.Crm;

/// <summary>
/// Puts a confirmed booking on its customer's record (#62). Bound to <c>crm.followup</c> in
/// <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>
/// The same <c>BookingConfirmed</c> also renders the traveller's documents and writes their email, on
/// another queue. Published messages fan out, so each queue gets its own copy and retries it on its
/// own: a customer record that fails to write does not make the documents render twice. Safe to
/// redeliver — an order already linked to a customer stays linked to it.
/// </remarks>
public sealed class BookingConfirmedCrmConsumer(
    TenantContext tenant,
    AuditContext audit,
    CustomerBookingRecorder recorder) : IConsumer<BookingConfirmed>
{
    public Task Consume(ConsumeContext<BookingConfirmed> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActingAs.Agency(tenant, audit, context.Message.AgencyId, context);
        return recorder.RecordAsync(context.Message, context.CancellationToken);
    }
}
