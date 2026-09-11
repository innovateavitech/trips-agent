using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Checkout;

/// <summary>Where a booking stands, in the console's four words.</summary>
public enum BookingState
{
    /// <summary>Paid for; the ticket is on its way.</summary>
    AwaitingTicket = 1,

    Ticketed = 2,

    /// <summary>Paid for, and it failed: waiting in the resolution queue.</summary>
    Failed = 3,

    /// <summary>Refunded, cancelled, or lapsed before it was paid for.</summary>
    Cancelled = 4,
}

/// <summary>One booking as the list shows it.</summary>
/// <param name="DepartsAt">The first departure, as an instant.</param>
/// <param name="TicketTimeLimit">Only while the booking is still waiting on the supplier.</param>
/// <param name="BookedAt">When it was paid for.</param>
public sealed record BookingSummaryView(
    string Reference,
    string LeadTraveller,
    int TravellerCount,
    SupplierProductType Product,
    string Origin,
    string Destination,
    string Carrier,
    DateTimeOffset DepartsAt,
    BookingState State,
    long SellMinor,
    string Currency,
    DateTimeOffset? TicketTimeLimit,
    string? Pnr,
    DateTimeOffset BookedAt);

public sealed record BookingTravellerView(TravellerType Type, string Name, string? TicketNumber);

/// <param name="DepartsAt">Local wall-clock time, <c>YYYY-MM-DDTHH:mm</c>, as the ticket prints it.</param>
public sealed record BookingSegmentView(string Carrier, string Origin, string Destination, string DepartsAt, string? ArrivesAt);

public sealed record BookingTimelineView(DateTimeOffset At, BookingState State, string Note);

/// <param name="AtRiskMinor">What the customer paid and has not yet got anything for.</param>
/// <param name="OpenedAt">When it went to the queue, so a slow resolution shows.</param>
public sealed record BookingFailureView(string Reason, long AtRiskMinor, OrderPaymentMethod PaidFrom, DateTimeOffset? OpenedAt);

/// <param name="NetMinor">What Trips charged the agency. The API shows it only to <c>margin.view</c>.</param>
/// <param name="MarkupMinor">What the agency added. As <paramref name="NetMinor"/>.</param>
public sealed record BookingDetailView(
    BookingSummaryView Summary,
    OrderPaymentMethod PaidFrom,
    IReadOnlyList<BookingTravellerView> Travellers,
    IReadOnlyList<BookingSegmentView> Segments,
    long NetMinor,
    long MarkupMinor,
    IReadOnlyList<BookingTimelineView> Timeline,
    BookingFailureView? Failure);

/// <summary>What the booking flow polls while the ticket is on its way.</summary>
public sealed record BookingProgressView(BookingState State, string? Pnr);

/// <summary>
/// The bookings screens' read side: the list, one booking, and its progress — the agency's own only,
/// by the tenant filter and row-level security.
/// </summary>
/// <remarks>
/// A booking is an order that was paid for. A price confirmation nobody went on to pay for is not one,
/// and does not appear.
/// </remarks>
public sealed class BookingQueries
{
    /// <summary>The list's length. The newest first.</summary>
    public const int ListLimit = 200;

    private readonly IAppDbContext _db;

    public BookingQueries(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<BookingSummaryView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var orders = await _db.Orders.AsNoTracking()
            .Where(order => order.PaidAt != null)
            .OrderByDescending(order => order.PaidAt)
            .Take(ListLimit)
            .Include(order => order.Lines)
            .ToListAsync(cancellationToken);

        var facts = await FactsAsync(orders, cancellationToken);
        return orders.Select(order => Summarise(order, facts)).ToList();
    }

    public async Task<BookingDetailView?> FindAsync(string reference, CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.OrderNumber == reference && candidate.PaidAt != null, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var facts = await FactsAsync([order], cancellationToken);
        var summary = Summarise(order, facts);
        var line = order.Lines[0];

        var history = await _db.OrderStatusHistory.AsNoTracking()
            .Where(entry => entry.OrderId == order.Id)
            .OrderBy(entry => entry.ChangedAt)
            .ToListAsync(cancellationToken);

        var timeline = history
            .Where(entry => entry.ToStatus != OrderStatus.PendingPayment)
            .Select(entry => new BookingTimelineView(entry.ChangedAt, StateOf(entry.ToStatus), entry.Reason ?? entry.ToStatus.ToString()))
            .ToList();

        var paidFrom = order.PaidFrom ?? OrderPaymentMethod.Wallet;

        var failure = summary.State == BookingState.Failed
            ? new BookingFailureView(line.FailureReason ?? "The supplier did not issue the ticket.", line.GrossAmountMinor.AmountMinor, paidFrom, line.ResolutionOpenedAt)
            : null;

        return new BookingDetailView(
            summary,
            paidFrom,
            facts.Travellers.GetValueOrDefault(line.Id, []),
            facts.Segments.GetValueOrDefault(line.SupplierOfferId ?? Guid.Empty, []),
            line.NetAmountMinor.AmountMinor,
            line.MarkupAmountMinor.AmountMinor,
            timeline,
            failure);
    }

    public async Task<BookingProgressView?> ProgressAsync(string reference, CancellationToken cancellationToken = default)
    {
        var detail = await FindAsync(reference, cancellationToken);
        return detail is null ? null : new BookingProgressView(detail.Summary.State, detail.Summary.Pnr);
    }

    /// <summary>
    /// The state the console shows, from the line and the supplier booking together. The line says
    /// whether money and a person are involved; the booking says where the supplier has got to.
    /// </summary>
    public static BookingState StateOf(OrderLine line, SupplierBookingStatus? booking)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.FulfilmentStatus switch
        {
            FulfilmentStatus.Confirmed => BookingState.Ticketed,
            FulfilmentStatus.FailedNeedsResolution => BookingState.Failed,
            FulfilmentStatus.Cancelled or FulfilmentStatus.Refunded => BookingState.Cancelled,
            _ when booking == SupplierBookingStatus.Ticketed => BookingState.Ticketed,
            _ => BookingState.AwaitingTicket,
        };
    }

    private static BookingState StateOf(OrderStatus status) => status switch
    {
        OrderStatus.Confirmed => BookingState.Ticketed,
        OrderStatus.PartiallyFailed => BookingState.Failed,
        OrderStatus.Cancelled or OrderStatus.Refunded => BookingState.Cancelled,
        _ => BookingState.AwaitingTicket,
    };

    private static BookingSummaryView Summarise(Order order, Facts facts)
    {
        var line = order.Lines[0];
        var booking = line.SupplierBookingId is { } id ? facts.Bookings.GetValueOrDefault(id) : null;
        var travellers = facts.Travellers.GetValueOrDefault(line.Id, []);
        var segments = facts.Segments.GetValueOrDefault(line.SupplierOfferId ?? Guid.Empty, []);
        var first = facts.FirstSegments.GetValueOrDefault(line.SupplierOfferId ?? Guid.Empty);
        var state = StateOf(line, booking?.Status);

        return new BookingSummaryView(
            order.OrderNumber,
            travellers.Count > 0 ? travellers[0].Name : string.Empty,
            travellers.Count,
            line.ItemType == OrderLineItemType.Bus ? SupplierProductType.Bus : SupplierProductType.Flight,
            first?.Origin ?? string.Empty,
            first?.Destination ?? string.Empty,
            segments.Count > 0 ? segments[0].Carrier : line.TitleSnapshot,
            first?.DepartsAt ?? order.PaidAt ?? order.CreatedAt,
            state,
            line.GrossAmountMinor.AmountMinor,
            order.Currency,
            state == BookingState.AwaitingTicket ? booking?.TicketTimeLimit : null,
            booking?.Pnr,
            order.PaidAt ?? order.CreatedAt);
    }

    /// <summary>Everything around a set of orders, fetched in a handful of queries rather than one per row.</summary>
    private async Task<Facts> FactsAsync(IReadOnlyList<Order> orders, CancellationToken cancellationToken)
    {
        var lineIds = orders.SelectMany(order => order.Lines).Select(line => line.Id).ToList();
        var bookingIds = orders.SelectMany(order => order.Lines).Select(line => line.SupplierBookingId).OfType<Guid>().ToList();
        var offerIds = orders.SelectMany(order => order.Lines).Select(line => line.SupplierOfferId).OfType<Guid>().ToList();

        var bookings = await _db.SupplierBookings.AsNoTracking()
            .Where(booking => bookingIds.Contains(booking.Id))
            .Select(booking => new BookingFact(booking.Id, booking.Status, booking.Pnr, booking.TicketTimeLimit))
            .ToDictionaryAsync(booking => booking.Id, cancellationToken);

        var tickets = await _db.SupplierBookingPassengers.AsNoTracking()
            .Where(passenger => bookingIds.Contains(passenger.SupplierBookingId) && passenger.TicketNumber != null)
            .Select(passenger => new { passenger.FirstName, passenger.LastName, passenger.TicketNumber })
            .ToListAsync(cancellationToken);

        var travellers = (await _db.OrderTravellers.AsNoTracking()
                .Where(traveller => lineIds.Contains(traveller.OrderLineId))
                .OrderBy(traveller => traveller.CreatedAt)
                .Select(traveller => new { traveller.OrderLineId, traveller.TravellerType, traveller.FirstName, traveller.LastName })
                .ToListAsync(cancellationToken))
            .GroupBy(traveller => traveller.OrderLineId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BookingTravellerView>)group
                    .Select(traveller => new BookingTravellerView(
                        traveller.TravellerType,
                        $"{traveller.FirstName} {traveller.LastName}",
                        tickets.FirstOrDefault(ticket =>
                            string.Equals(ticket.FirstName, traveller.FirstName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ticket.LastName, traveller.LastName, StringComparison.OrdinalIgnoreCase))?.TicketNumber))
                    .ToList());

        var flights = await _db.FlightSegments.AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .OrderBy(segment => segment.LegIndex)
            .ThenBy(segment => segment.SegmentIndex)
            .Select(segment => new { segment.SupplierOfferId, segment.LegIndex, segment.FlightNumber, segment.OriginIata, segment.DestinationIata, segment.DepartureAt, segment.ArrivalAt })
            .ToListAsync(cancellationToken);

        var buses = await _db.BusSegments.AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .OrderBy(segment => segment.DepartureAt)
            .Select(segment => new { segment.SupplierOfferId, segment.OperatorName, segment.DepartureTerminalId, segment.ArrivalTerminalId, segment.DepartureAt, segment.ArrivalAt })
            .ToListAsync(cancellationToken);

        var segments = new Dictionary<Guid, IReadOnlyList<BookingSegmentView>>();
        var firsts = new Dictionary<Guid, FirstSegment>();

        foreach (var group in flights.GroupBy(segment => segment.SupplierOfferId))
        {
            var ordered = group.ToList();
            var outbound = ordered.Where(segment => segment.LegIndex == ordered[0].LegIndex).ToList();

            segments[group.Key] = ordered
                .Select(segment => new BookingSegmentView(
                    segment.FlightNumber,
                    segment.OriginIata,
                    segment.DestinationIata,
                    Airports.WallClock(segment.DepartureAt, segment.OriginIata),
                    Airports.WallClock(segment.ArrivalAt, segment.DestinationIata)))
                .ToList();

            firsts[group.Key] = new FirstSegment(outbound[0].OriginIata, outbound[^1].DestinationIata, outbound[0].DepartureAt);
        }

        foreach (var group in buses.GroupBy(segment => segment.SupplierOfferId))
        {
            var ordered = group.ToList();

            segments[group.Key] = ordered
                .Select(segment => new BookingSegmentView(
                    segment.OperatorName,
                    segment.DepartureTerminalId,
                    segment.ArrivalTerminalId,
                    Airports.WallClock(segment.DepartureAt, "LOS"),
                    segment.ArrivalAt is { } arrives ? Airports.WallClock(arrives, "LOS") : null))
                .ToList();

            firsts[group.Key] = new FirstSegment(ordered[0].DepartureTerminalId, ordered[0].ArrivalTerminalId, ordered[0].DepartureAt);
        }

        return new Facts(bookings, travellers, segments, firsts);
    }

    private sealed record BookingFact(Guid Id, SupplierBookingStatus Status, string? Pnr, DateTimeOffset? TicketTimeLimit);

    private sealed record FirstSegment(string Origin, string Destination, DateTimeOffset DepartsAt);

    private sealed record Facts(
        Dictionary<Guid, BookingFact> Bookings,
        Dictionary<Guid, IReadOnlyList<BookingTravellerView>> Travellers,
        Dictionary<Guid, IReadOnlyList<BookingSegmentView>> Segments,
        Dictionary<Guid, FirstSegment> FirstSegments);
}
