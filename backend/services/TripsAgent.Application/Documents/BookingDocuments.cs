using TripsAgent.Application.Messaging;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Application.Documents;

/// <summary>
/// Asks for an order's invoice and vouchers once its lines are confirmed (#46).
/// </summary>
/// <remarks>
/// <c>BookingFollowUps</c> calls this when the pipeline announces <c>BookingConfirmed</c>. That event
/// exists only if the confirmation committed, so there are no documents for a confirmation that rolled
/// back; and it is not acknowledged until this request is saved, so there is no confirmed order that
/// never gets its documents. Everything slow — numbering, drawing, storing, emailing — happens
/// afterwards in the Worker, from <c>documents.render</c>.
/// </remarks>
public interface IBookingDocuments
{
    /// <summary>
    /// Stages a request to issue <paramref name="orderId"/>'s invoice and a voucher for each of its
    /// confirmed lines, render them in the customer's currency, and email them to
    /// <paramref name="customer"/>. Nothing happens until the caller saves.
    /// </summary>
    /// <remarks>
    /// Safe to call again — after a retry, or when a later line is confirmed. Documents that already
    /// exist are left alone and the email is sent once; only what is missing is issued.
    /// </remarks>
    /// <returns>False when the caller cannot see the order — another agency's, or none — and nothing was staged.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> IssueForOrderAsync(Guid orderId, BookingCustomer customer, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BookingDocuments(
    IAppDbContext db,
    IOutbox outbox,
    ITenantContext tenant,
    IPlatformScope platformScope) : IBookingDocuments
{
    public async Task<bool> IssueForOrderAsync(Guid orderId, BookingCustomer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentException.ThrowIfNullOrWhiteSpace(customer.Name);

        var agencyId = await OrderScope.AgencyOfAsync(db, tenant, platformScope, orderId, cancellationToken);

        if (agencyId is not { } owner)
        {
            return false;
        }

        outbox.Enqueue(
            new OrderDocumentsRequested(
                owner,
                orderId,
                customer.Name.Trim(),
                string.IsNullOrWhiteSpace(customer.Email) ? null : customer.Email.Trim()),
            owner);

        return true;
    }
}
