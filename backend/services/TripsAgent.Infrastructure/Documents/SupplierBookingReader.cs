using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Documents;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Documents;

/// <summary>
/// <see cref="ISupplierBookingReader"/> over the scoped <see cref="AppDbContext"/>, so the tenant
/// filter and row-level security apply exactly as they do to everything else.
/// </summary>
public sealed class SupplierBookingReader(AppDbContext db) : ISupplierBookingReader
{
    public async Task<IReadOnlyList<SupplierBookingSnapshot>> ForOrderLinesAsync(
        IReadOnlyCollection<Guid> orderLineIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderLineIds);

        if (orderLineIds.Count == 0)
        {
            return [];
        }

        var bookings = await db.SupplierBookings
            .AsNoTracking()
            .Where(booking => orderLineIds.Contains(booking.OrderLineId))
            .Select(booking => new { booking.Id, booking.OrderLineId, booking.Pnr })
            .ToListAsync(cancellationToken);

        var bookingIds = bookings.Select(booking => booking.Id).ToList();

        // Ordered by id: ids are UUID v7, so this is the order the travellers were entered in, which
        // puts the lead traveller first on the voucher.
        var passengers = await db.SupplierBookingPassengers
            .AsNoTracking()
            .Where(passenger => bookingIds.Contains(passenger.SupplierBookingId))
            .OrderBy(passenger => passenger.Id)
            .ToListAsync(cancellationToken);

        return bookings
            .Select(booking => new SupplierBookingSnapshot(
                booking.OrderLineId,
                booking.Pnr,
                passengers
                    .Where(passenger => passenger.SupplierBookingId == booking.Id)
                    .Select(passenger => new SupplierPassengerSnapshot(
                        passenger.Title,
                        passenger.FirstName,
                        passenger.MiddleName,
                        passenger.LastName,
                        passenger.PassengerType,
                        passenger.TicketNumber,
                        passenger.SeatNumbers))
                    .ToList()))
            .ToList();
    }
}
