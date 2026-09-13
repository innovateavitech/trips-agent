using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Crm;

namespace TripsAgent.Application.Crm;

/// <summary>Who a confirmed booking is for, as the checkout recorded them.</summary>
/// <param name="Name">The lead traveller's name.</param>
internal sealed record BookingContact(string Name, string? Email, string? Phone);

/// <summary>
/// Records a confirmed booking against the agency's customer record (#62, FRD §2.8 RS-1): the
/// "booking" half of "a customer 360 auto-created from any inquiry, quote or booking".
/// </summary>
/// <remarks>
/// <para>
/// Hooked to <see cref="BookingConfirmed"/>, which the checkout publishes through the outbox — so
/// there is a customer record only for a booking that really committed. The traveller is matched to a
/// customer by email, then phone, exactly as an inquiry is (<see cref="CustomerDirectory"/>): the same
/// person who asked about Dubai in March and booked it in April is one record, not two.
/// </para>
/// <para>
/// <b>Safe to run again.</b> The order carries the customer it was linked to, so a redelivered event
/// finds it already linked and only touches the customer's last-activity time. An order the checkout
/// already knew the customer for — one placed from the console against an existing record — is left
/// alone.
/// </para>
/// </remarks>
public sealed partial class CustomerBookingRecorder
{
    private readonly IAppDbContext _db;
    private readonly CustomerDirectory _customers;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerBookingRecorder> _logger;

    public CustomerBookingRecorder(
        IAppDbContext db,
        CustomerDirectory customers,
        IUniqueViolationDetector uniqueViolations,
        TimeProvider clock,
        ILogger<CustomerBookingRecorder> logger)
    {
        _db = db;
        _customers = customers;
        _uniqueViolations = uniqueViolations;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Puts the confirmed booking on its customer's record, creating the customer if it is new.</summary>
    /// <param name="confirmed">The event. Its agency is already the caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    public async Task RecordAsync(BookingConfirmed confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        var order = await _db.Orders.FirstOrDefaultAsync(candidate => candidate.Id == confirmed.OrderId, cancellationToken);

        if (order is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();

        // Already linked — by an earlier line of the same order, by a redelivery of this event, or by
        // a console checkout that knew the customer. Nothing to decide; just mark them active.
        if (order.CustomerId is { } existingId)
        {
            if (await _db.Customers.FirstOrDefaultAsync(candidate => candidate.Id == existingId, cancellationToken)
                is { } existing)
            {
                existing.RecordActivity(now);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var contact = await ContactAsync(confirmed.OrderLineId, cancellationToken);

        if (contact is null)
        {
            // Nothing to match on and nothing to name a record after. The booking stands; the agent
            // can key the customer in from the lead that produced it.
            BookingWithoutContact(_logger, confirmed.OrderNumber);
            return;
        }

        await CrmSaves.RetryOnceOnUniqueViolationAsync(
            _db,
            _uniqueViolations,
            async () =>
            {
                var customer = await _customers.FindOrAddAsync(
                    confirmed.AgencyId, contact.Name, contact.Email, contact.Phone, now, cancellationToken);

                order.LinkCustomer(customer.Id);
                customer.RecordActivity(now);
                return customer;
            },
            cancellationToken);
    }

    /// <summary>
    /// The line's lead traveller, with the contact details the checkout took: the first with an email
    /// address, an adult before a child, and otherwise the first traveller there is.
    /// </summary>
    /// <remarks>
    /// Read from the supplier booking's passengers, which carry email and phone; the order's own
    /// traveller rows carry names only, and are the fallback. A name alone is not enough to recognise
    /// the same person later, so a traveller with neither email nor phone is not made a customer.
    /// </remarks>
    private async Task<BookingContact?> ContactAsync(Guid orderLineId, CancellationToken cancellationToken)
    {
        // Ids are version 7 GUIDs, which sort by when they were made: the newest booking is the one
        // that counts when a line's price was confirmed more than once.
        var bookingId = await _db.SupplierBookings.AsNoTracking()
            .Where(booking => booking.OrderLineId == orderLineId)
            .OrderByDescending(booking => booking.Id)
            .Select(booking => (Guid?)booking.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (bookingId is null)
        {
            return null;
        }

        var passengers = await _db.SupplierBookingPassengers.AsNoTracking()
            .Where(passenger => passenger.SupplierBookingId == bookingId.Value)
            .OrderBy(passenger => passenger.PassengerType)
            .ThenBy(passenger => passenger.Id)
            .Select(passenger => new
            {
                passenger.FirstName,
                passenger.LastName,
                passenger.Email,
                passenger.PhoneNumber,
            })
            .ToListAsync(cancellationToken);

        var lead = passengers.FirstOrDefault(passenger => !string.IsNullOrWhiteSpace(passenger.Email))
                   ?? passengers.FirstOrDefault(passenger => !string.IsNullOrWhiteSpace(passenger.PhoneNumber));

        if (lead is null)
        {
            return null;
        }

        var name = $"{lead.FirstName?.Trim()} {lead.LastName?.Trim()}".Trim();

        return name.Length == 0 ? null : new BookingContact(name, lead.Email, lead.PhoneNumber);
    }

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Information,
        Message = "Booking {OrderNumber} has no traveller with an email address or phone number, so it was not put on a customer record.")]
    private static partial void BookingWithoutContact(ILogger logger, string orderNumber);
}
