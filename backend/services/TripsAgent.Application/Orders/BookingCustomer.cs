using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;

namespace TripsAgent.Application.Orders;

/// <summary>The customer an order's documents and emails are for.</summary>
/// <param name="Name">How to greet them, and the name their documents are made out to.</param>
/// <param name="Email">
/// Where their email goes. Null when there is none: documents are still issued, and the agent can
/// download them, but nothing is emailed.
/// </param>
/// <remarks>
/// An order records no customer contact of its own yet — a guest checkout has no customer record at
/// all — so <see cref="BookingCustomers.LeadTravellerAsync"/> finds one among the travellers the agent
/// entered at checkout.
/// </remarks>
public sealed record BookingCustomer(string Name, string? Email);

/// <summary>Finds who an order line's traveller emails and documents are for.</summary>
public static class BookingCustomers
{
    /// <summary>
    /// The line's lead traveller, as the agent entered them at checkout: the first traveller with an
    /// email address, an adult before a child — or, when none has one, the first traveller, with none.
    /// </summary>
    /// <remarks>
    /// Read from the supplier booking's passengers, which carry the contact details the checkout took;
    /// the order's own traveller rows carry names only, and are the fallback. A line whose price was
    /// confirmed more than once has more than one supplier booking, and the newest is the one that
    /// counts. Read in the caller's scope, so another agency's line finds no one.
    /// </remarks>
    /// <param name="db">The caller's unit of work.</param>
    /// <param name="orderLineId">The line.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Null when the caller can see no traveller on the line.</returns>
    public static async Task<BookingCustomer?> LeadTravellerAsync(
        IAppDbContext db,
        Guid orderLineId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Ids are version 7 GUIDs, which sort by when they were made.
        var supplierBookingId = await db.SupplierBookings
            .AsNoTracking()
            .Where(booking => booking.OrderLineId == orderLineId)
            .OrderByDescending(booking => booking.Id)
            .Select(booking => (Guid?)booking.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (supplierBookingId is { } bookingId)
        {
            var passengers = await db.SupplierBookingPassengers
                .AsNoTracking()
                .Where(passenger => passenger.SupplierBookingId == bookingId)
                .OrderBy(passenger => passenger.PassengerType)
                .ThenBy(passenger => passenger.Id)
                .Select(passenger => new { passenger.FirstName, passenger.LastName, passenger.Email })
                .ToListAsync(cancellationToken);

            var lead = passengers.FirstOrDefault(passenger => !string.IsNullOrWhiteSpace(passenger.Email))
                       ?? passengers.FirstOrDefault();

            if (lead is not null)
            {
                return new BookingCustomer(
                    NameOf(lead.FirstName, lead.LastName),
                    string.IsNullOrWhiteSpace(lead.Email) ? null : lead.Email.Trim());
            }
        }

        var traveller = await db.OrderTravellers
            .AsNoTracking()
            .Where(candidate => candidate.OrderLineId == orderLineId)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => new { candidate.FirstName, candidate.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        return traveller is null ? null : new BookingCustomer(NameOf(traveller.FirstName, traveller.LastName), null);
    }

    private static string NameOf(string firstName, string lastName) => $"{firstName.Trim()} {lastName.Trim()}".Trim();
}
