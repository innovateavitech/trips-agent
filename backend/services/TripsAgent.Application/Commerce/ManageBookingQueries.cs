using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Documents;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Commerce;

/// <summary>
/// The "manage my booking" page behind a traveller's link (build plan F5, decision 21).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a traveller may see, and nothing more.</b> What they bought, where each item has got to,
/// what they paid, what is still to pay, and their documents. Never the net rate, the markup, the
/// platform fee or the agency's margin: that is the agent's commercial information, and a traveller
/// who could read it could work out what their agent pays.
/// </para>
/// <para>
/// <b>Nothing here names Trips</b> (CLAUDE.md rule 4). The page carries the agency's own name and
/// contact details, because as far as the traveller is concerned the agency is who they bought from.
/// </para>
/// <para>
/// <b>Honest about failure.</b> A line in the resolution queue reads as needing attention, with what
/// the agency is doing about it — never as confirmed, and never as a silent gap in the list. That is
/// the traveller's half of what the agent's resolution queue promises.
/// </para>
/// </remarks>
public sealed class ManageBookingQueries
{
    private readonly IAppDbContext _db;
    private readonly BookingAccessLinks _links;
    private readonly StorefrontTenant _storefront;
    private readonly DocumentLinks _documents;

    public ManageBookingQueries(
        IAppDbContext db,
        BookingAccessLinks links,
        StorefrontTenant storefront,
        DocumentLinks documents)
    {
        _db = db;
        _links = links;
        _storefront = storefront;
        _documents = documents;
    }

    /// <summary>The booking a link opens, or a plain "not found" when it opens nothing.</summary>
    /// <param name="host">The host name the traveller's browser used.</param>
    /// <param name="secret">The secret out of their link.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<StoreResult<ManageBookingResponse>> OpenAsync(
        string? host,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        var agency = await _storefront.EnterAsync(host, cancellationToken);

        if (agency is null)
        {
            return Store.NotFound<ManageBookingResponse>(NoSuchBooking);
        }

        // Read inside the agency the host resolved to, so a link for one agency's booking presented
        // on another's domain finds nothing — and reads exactly like a link that has lapsed.
        var orderId = await _links.OrderForAsync(secret, cancellationToken);

        if (orderId is null)
        {
            return Store.NotFound<ManageBookingResponse>(NoSuchBooking);
        }

        var order = await _db.Orders.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .FirstOrDefaultAsync(candidate => candidate.Id == orderId.Value, cancellationToken);

        if (order is null)
        {
            return Store.NotFound<ManageBookingResponse>(NoSuchBooking);
        }

        var lineIds = order.Lines.Select(line => line.Id).ToList();

        var pnrs = await _db.SupplierBookings.AsNoTracking()
            .Where(booking => lineIds.Contains(booking.OrderLineId))
            .Select(booking => new { booking.OrderLineId, booking.Pnr })
            .ToListAsync(cancellationToken);

        var schedules = await _db.BookingPaymentSchedules.AsNoTracking()
            .Include(schedule => schedule.Items)
            .Where(schedule => lineIds.Contains(schedule.OrderLineId))
            .ToListAsync(cancellationToken);

        var documents = await _db.GeneratedDocuments.AsNoTracking()
            .Where(document => document.OrderId == order.Id && document.Status == DocumentStatus.Ready)
            .OrderBy(document => document.IssuedAt)
            .Select(document => new { document.Id, document.DocumentType, document.DocumentNumber })
            .ToListAsync(cancellationToken);

        // Who the traveller replies to. The agency's own owner, because an agency has no separate
        // support address of its own yet — and never one of ours (CLAUDE.md rule 4).
        var contact = await _db.Users.AsNoTracking()
            .Where(user => user.AgencyId == order.AgencyId)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Email, user.PhoneNumber })
            .FirstOrDefaultAsync(cancellationToken);

        return new StoreResult<ManageBookingResponse>.Done(new ManageBookingResponse(
            order.OrderNumber,
            StatusOf(order),
            agency.Name,
            contact?.Email,
            contact?.PhoneNumber,
            order.PlacedAt ?? order.CreatedAt,
            order.Currency,
            order.TotalGrossMinor.AmountMinor,
            await PaidAsync(order, cancellationToken),
            order.Lines
                .OrderBy(line => line.CreatedAt)
                .Select(line => new BookingLineResponse(
                    line.Id,
                    line.ItemType.ToString(),
                    line.TitleSnapshot,
                    StatusOf(line),
                    DetailOf(line),
                    line.GrossAmountMinor.AmountMinor,
                    pnrs.FirstOrDefault(booking => booking.OrderLineId == line.Id)?.Pnr))
                .ToList(),
            schedules
                .SelectMany(schedule => schedule.Items)
                .OrderBy(item => item.DueDate)
                .ThenBy(item => item.Sequence)
                .Select(item => new BookingInstalmentResponse(
                    item.Sequence,
                    item.Label,
                    item.DueDate,
                    item.AmountMinor.AmountMinor,
                    item.State.ToString()))
                .ToList(),
            documents
                .Select(document => new ManageBookingDocumentResponse(
                    document.Id,
                    document.DocumentType.ToString(),
                    document.DocumentNumber,
                    _documents.PublicPathFor(document.Id)))
                .ToList()));
    }

    /// <summary>What the traveller has actually paid, from the payments that were credited.</summary>
    /// <remarks>
    /// Read from the payment rows rather than from the order's totals: a deposit on a departure means
    /// the two differ, and the figure a traveller cares about is the one that left their card.
    /// </remarks>
    private async Task<long> PaidAsync(Order order, CancellationToken cancellationToken)
    {
        var paid = await _db.PaymentTransactions.AsNoTracking()
            .Where(payment => payment.OrderId == order.Id && payment.LedgerTransactionGroupId != null)
            .Select(payment => payment.AmountMinor)
            .ToListAsync(cancellationToken);

        return paid.Sum(amount => amount.AmountMinor);
    }

    /// <summary>The booking as a whole, in one word a traveller understands.</summary>
    private static string StatusOf(Order order) => order.Status switch
    {
        OrderStatus.PendingPayment => "AwaitingPayment",
        OrderStatus.Confirmed => "Confirmed",
        OrderStatus.PartiallyFulfilled => "PartlyConfirmed",
        OrderStatus.PartiallyFailed => "NeedsAttention",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Refunded => "Refunded",

        // Paid, and every line still on its way. "Confirming" would promise more than we know.
        _ => "PartlyConfirmed",
    };

    /// <summary>One line, in one word.</summary>
    private static string StatusOf(OrderLine line) => line.FulfilmentStatus switch
    {
        FulfilmentStatus.Confirmed => "Confirmed",
        FulfilmentStatus.FailedNeedsResolution => "NeedsAttention",
        FulfilmentStatus.Cancelled => "Cancelled",
        FulfilmentStatus.Refunded => "Refunded",
        _ => "Pending",
    };

    /// <summary>
    /// What is happening with a line, said plainly.
    /// </summary>
    /// <remarks>
    /// A failed line does <b>not</b> repeat the supplier's own words at the traveller: those are
    /// written for an agent, and often name the supplier. It says what is true and useful — somebody
    /// is on it — and leaves the detail to the agency, who is the one who will call them.
    /// </remarks>
    private static string? DetailOf(OrderLine line) => line.FulfilmentStatus switch
    {
        FulfilmentStatus.FailedNeedsResolution when line.ResolutionStatus == ResolutionStatus.ResolvedRefunded =>
            "This item could not be completed, and has been refunded.",

        FulfilmentStatus.FailedNeedsResolution =>
            "This item could not be completed. Your travel agent is sorting it out and will be in touch; "
            + "nothing further is needed from you.",

        FulfilmentStatus.Refunded => "This item has been refunded.",
        FulfilmentStatus.Cancelled => "This item was cancelled.",
        FulfilmentStatus.Confirming => "We are confirming this with the operator.",
        FulfilmentStatus.Pending => "Waiting for payment.",
        _ => null,
    };

    private const string NoSuchBooking = "We could not find that booking. The link may have expired.";
}
