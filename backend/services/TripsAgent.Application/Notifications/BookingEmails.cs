using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Notifications;

/// <summary>Money that went back for an order line, as the traveller's refund notice describes it.</summary>
/// <param name="RefundId">The refund. One notice per refund, however many times it is announced.</param>
/// <param name="Method">How it went back. Only <see cref="RefundMethod.Gateway"/> reaches the traveller's own card.</param>
/// <param name="AmountMinor">How much, in minor units. Named to the traveller only for a card refund.</param>
/// <param name="Currency">ISO 4217, e.g. <c>NGN</c>.</param>
public sealed record BookingRefund(Guid RefundId, RefundMethod Method, long AmountMinor, string Currency);

/// <summary>
/// The traveller's emails about one item of their booking (#42–#45): it is confirmed, it needs
/// attention, or it was cancelled and its money went back.
/// </summary>
/// <remarks>
/// All three are traveller-facing, so they go out under the agency's brand and never ours (CLAUDE.md
/// rule 4). Each stages the email in the caller's unit of work — it is sent only if the caller's save
/// commits — and each is keyed, so an event delivered three times emails once.
/// <see cref="BookingFollowUps"/> calls them from the booking pipeline's events.
/// </remarks>
public interface IBookingEmails
{
    /// <summary>Stages "your booking is confirmed" for one confirmed order line.</summary>
    /// <returns>False when nothing was staged: no email address, the line is not visible, or it was already sent.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> SendBookingConfirmedAsync(Guid orderLineId, BookingCustomer customer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages "one item in your booking needs attention" for a line that could not be ticketed and waits
    /// in the agent's resolution queue (#44).
    /// </summary>
    /// <param name="orderLineId">The line.</param>
    /// <param name="customer">Who to write to.</param>
    /// <param name="flaggedAt">
    /// When the line was flagged. The same flag announced again emails once; a line flagged again after
    /// a later failure is news, and emails again.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>False when nothing was staged: no email address, the line is not visible, or it was already sent.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> SendItemNeedsAttentionAsync(
        Guid orderLineId,
        BookingCustomer customer,
        DateTimeOffset flaggedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages "an item in your booking has been cancelled", saying what happens to the money (#43, #44).
    /// </summary>
    /// <returns>False when nothing was staged: no email address, the line is not visible, or this refund was already announced.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> SendRefundNoticeAsync(
        Guid orderLineId,
        BookingCustomer customer,
        BookingRefund refund,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BookingEmails(
    IAppDbContext db,
    INotifier notifier,
    ITenantContext tenant,
    IPlatformScope platformScope) : IBookingEmails
{
    public async Task<bool> SendBookingConfirmedAsync(Guid orderLineId, BookingCustomer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var item = await DescribeAsync(orderLineId, cancellationToken);

        if (item is null || string.IsNullOrWhiteSpace(customer.Email))
        {
            return false;
        }

        return await QueueAsync(
            item,
            NotificationTemplateCatalog.BookingConfirmed,
            customer.Email,
            customer.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = item.Reference,
                ["itinerarySummary"] = item.Title,
                ["travellerNames"] = item.TravellerNames ?? customer.Name,
                ["departureDate"] = item.DepartureDate ?? "as shown on your voucher",
            },
            $"{NotificationTemplateCatalog.BookingConfirmed}:{orderLineId}",
            cancellationToken);
    }

    public async Task<bool> SendItemNeedsAttentionAsync(
        Guid orderLineId,
        BookingCustomer customer,
        DateTimeOffset flaggedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var item = await DescribeAsync(orderLineId, cancellationToken);

        if (item is null || string.IsNullOrWhiteSpace(customer.Email))
        {
            return false;
        }

        // No reason is passed on. The pipeline's is written for the agent — a supplier status code — and
        // the traveller needs only what it means for them.
        return await QueueAsync(
            item,
            NotificationTemplateCatalog.BookingNeedsAttention,
            customer.Email,
            customer.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = item.Reference,
                ["itemTitle"] = item.Title,
            },
            string.Create(
                CultureInfo.InvariantCulture,
                $"{NotificationTemplateCatalog.BookingNeedsAttention}:{orderLineId}:{flaggedAt.UtcTicks}"),
            cancellationToken);
    }

    public async Task<bool> SendRefundNoticeAsync(
        Guid orderLineId,
        BookingCustomer customer,
        BookingRefund refund,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(refund);

        var item = await DescribeAsync(orderLineId, cancellationToken);

        if (item is null || string.IsNullOrWhiteSpace(customer.Email))
        {
            return false;
        }

        return await QueueAsync(
            item,
            NotificationTemplateCatalog.BookingRefundNotice,
            customer.Email,
            customer.Name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bookingReference"] = item.Reference,
                ["itemTitle"] = item.Title,
                ["refundDetail"] = DescribeRefund(refund),
            },
            $"{NotificationTemplateCatalog.BookingRefundNotice}:{refund.RefundId}",
            cancellationToken);
    }

    /// <summary>What the refund notice says about the money.</summary>
    /// <remarks>
    /// An amount only when it went back to the card the traveller paid with: that is their own money
    /// coming back. Anything else went back to the agency's wallet, and is what the <i>agency</i> paid —
    /// its net rate, which a traveller is never shown, and never the price the traveller was quoted.
    /// </remarks>
    /// <param name="refund">The money that went back.</param>
    /// <returns>One or two sentences for the traveller.</returns>
    public static string DescribeRefund(BookingRefund refund)
    {
        ArgumentNullException.ThrowIfNull(refund);

        return refund.Method == RefundMethod.Gateway
            ? $"{DocumentMoney.Format(refund.AmountMinor, refund.Currency)} has been refunded to the card you paid with. "
              + "Your bank may take a few working days to show it. Reply to this email if you have any questions."
            : "If you have already paid for it, reply to this email and we will sort out your refund.";
    }

    private Task<bool> QueueAsync(
        ItemDescription item,
        string templateKey,
        string email,
        string name,
        Dictionary<string, string> variables,
        string dedupeKey,
        CancellationToken cancellationToken) =>
        notifier.QueueEmailAsync(
            new EmailNotificationRequest(item.AgencyId, templateKey, email, name, variables, DedupeKey: dedupeKey),
            cancellationToken);

    /// <summary>What the emails say about one order line, read in the caller's own scope.</summary>
    private async Task<ItemDescription?> DescribeAsync(Guid orderLineId, CancellationToken cancellationToken)
    {
        OrderScope.EnsureScoped(tenant, platformScope);

        var line = await db.OrderLines
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == orderLineId, cancellationToken);

        if (line is null)
        {
            return null;
        }

        var order = await db.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == line.OrderId)
            .Select(candidate => new { candidate.AgencyId, candidate.OrderNumber })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        var travellers = await db.OrderTravellers
            .AsNoTracking()
            .Where(traveller => traveller.OrderLineId == line.Id)
            .OrderBy(traveller => traveller.Id)
            .Select(traveller => traveller.FirstName + " " + traveller.LastName)
            .ToListAsync(cancellationToken);

        // The line's first departure, in the local time it was recorded in — the time on the ticket,
        // not a conversion of it.
        DateTimeOffset? departure = null;

        if (line.SupplierOfferId is { } offerId)
        {
            var flights = await db.FlightSegments
                .AsNoTracking()
                .Where(segment => segment.SupplierOfferId == offerId)
                .Select(segment => segment.DepartureAt)
                .ToListAsync(cancellationToken);

            var buses = await db.BusSegments
                .AsNoTracking()
                .Where(segment => segment.SupplierOfferId == offerId)
                .Select(segment => segment.DepartureAt)
                .ToListAsync(cancellationToken);

            departure = flights.Concat(buses).OrderBy(at => at.UtcDateTime).Cast<DateTimeOffset?>().FirstOrDefault();
        }

        return new ItemDescription(
            order.AgencyId,
            order.OrderNumber,
            line.TitleSnapshot,
            travellers.Count == 0 ? null : string.Join(", ", travellers.Distinct(StringComparer.Ordinal)),
            departure?.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture));
    }

    private sealed record ItemDescription(
        Guid AgencyId,
        string Reference,
        string Title,
        string? TravellerNames,
        string? DepartureDate);
}
