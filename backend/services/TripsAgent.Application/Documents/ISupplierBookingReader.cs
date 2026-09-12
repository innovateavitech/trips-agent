using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Documents;

/// <summary>One traveller as the supplier booked them.</summary>
/// <param name="SeatNumbersJson">A JSON array of seats, as the supplier listed them, or null.</param>
public sealed record SupplierPassengerSnapshot(
    string? Title,
    string FirstName,
    string? MiddleName,
    string LastName,
    PassengerType PassengerType,
    string? TicketNumber,
    string? SeatNumbersJson);

/// <summary>What the supplier booked for one order line: its reference and who is on it.</summary>
/// <param name="OrderLineId">The line it fulfils.</param>
/// <param name="Pnr">The airline's or operator's reference, once there is one.</param>
/// <param name="Passengers">Who is booked, with ticket numbers once issued.</param>
public sealed record SupplierBookingSnapshot(
    Guid OrderLineId,
    string? Pnr,
    IReadOnlyList<SupplierPassengerSnapshot> Passengers);

/// <summary>
/// Reads the supplier's side of an order for its vouchers — the PNR and the ticket numbers.
/// </summary>
/// <remarks>
/// A narrow read-only port rather than new sets on <c>IAppDbContext</c>: the booking pipeline
/// (#36–#38) owns the supplier booking tables and how they are written, and a voucher only ever
/// needs to read three things from them. Goes through the ordinary tenant filter, so it only ever
/// sees the current agency's bookings.
/// </remarks>
public interface ISupplierBookingReader
{
    public Task<IReadOnlyList<SupplierBookingSnapshot>> ForOrderLinesAsync(
        IReadOnlyCollection<Guid> orderLineIds,
        CancellationToken cancellationToken = default);
}
