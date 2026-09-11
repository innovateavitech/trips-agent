using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Orders;

namespace TripsAgent.Application.Notifications;

/// <summary>Why a booking needs its traveller to act, in words for the traveller.</summary>
/// <param name="Reason">
/// A short code for the reason, e.g. <c>price-changed</c>. The same reason for the same order is
/// emailed once, however many times it is asked for; a different reason is a different email.
/// </param>
/// <param name="WhatHappened">One sentence the traveller will read: what went wrong.</param>
/// <param name="WhatToDo">One sentence: what they need to do about it.</param>
/// <param name="RespondBy">The deadline. Printed in the agency's own time zone.</param>
public sealed record BookingAttention(string Reason, string WhatHappened, string WhatToDo, DateTimeOffset RespondBy);

/// <summary>
/// For the checkout saga (#42): the traveller's "booking confirmed" and "booking needs attention" emails.
/// </summary>
/// <remarks>
/// Both are traveller-facing, so they go out under the agency's brand and never ours (CLAUDE.md
/// rule 4). Both stage the email in the caller's unit of work — it is sent only if the caller's save
/// commits — and both are keyed so that a saga step replayed three times emails once.
/// </remarks>
public interface IBookingEmails
{
    /// <summary>Stages "your booking is confirmed" for <paramref name="orderId"/>'s customer.</summary>
    /// <returns>False when nothing was staged: no email address, the order is not visible, or it was already sent.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> SendBookingConfirmedAsync(Guid orderId, BookingCustomer customer, CancellationToken cancellationToken = default);

    /// <summary>Stages "your booking needs your attention" for <paramref name="orderId"/>'s customer.</summary>
    /// <returns>False when nothing was staged: no email address, the order is not visible, or this reason was already sent.</returns>
    /// <exception cref="InvalidOperationException">Called acting for no agency and outside any platform scope.</exception>
    public Task<bool> SendBookingNeedsAttentionAsync(
        Guid orderId,
        BookingCustomer customer,
        BookingAttention attention,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BookingEmails(
    IAppDbContext db,
    INotifier notifier,
    ITenantContext tenant,
    IPlatformScope platformScope) : IBookingEmails
{
    public async Task<bool> SendBookingConfirmedAsync(Guid orderId, BookingCustomer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var booking = await DescribeAsync(orderId, cancellationToken);

        if (booking is null || string.IsNullOrWhiteSpace(customer.Email))
        {
            return false;
        }

        return await notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                booking.AgencyId,
                NotificationTemplateCatalog.BookingConfirmed,
                customer.Email,
                customer.Name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = booking.Reference,
                    ["itinerarySummary"] = booking.Itinerary,
                    ["travellerNames"] = booking.TravellerNames ?? customer.Name,
                    ["departureDate"] = booking.DepartureDate ?? "as shown on your voucher",
                },
                DedupeKey: $"{NotificationTemplateCatalog.BookingConfirmed}:{orderId}"),
            cancellationToken);
    }

    public async Task<bool> SendBookingNeedsAttentionAsync(
        Guid orderId,
        BookingCustomer customer,
        BookingAttention attention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentException.ThrowIfNullOrWhiteSpace(attention.Reason);

        var booking = await DescribeAsync(orderId, cancellationToken);

        if (booking is null || string.IsNullOrWhiteSpace(customer.Email))
        {
            return false;
        }

        return await notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                booking.AgencyId,
                NotificationTemplateCatalog.BookingNeedsAttention,
                customer.Email,
                customer.Name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = booking.Reference,
                    ["itinerarySummary"] = booking.Itinerary,
                    ["whatHappened"] = attention.WhatHappened,
                    ["whatToDo"] = attention.WhatToDo,
                    ["deadline"] = InAgencyTime(attention.RespondBy, booking.TimeZoneId),
                },
                DedupeKey: $"{NotificationTemplateCatalog.BookingNeedsAttention}:{orderId}:{attention.Reason.Trim()}"),
            cancellationToken);
    }

    /// <summary>What the two emails say about the order, read in the caller's own scope.</summary>
    private async Task<BookingDescription?> DescribeAsync(Guid orderId, CancellationToken cancellationToken)
    {
        OrderScope.EnsureScoped(tenant, platformScope);

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return null;
        }

        var timeZoneId = await db.Agencies
            .AsNoTracking()
            .Where(agency => agency.Id == order.AgencyId)
            .Select(agency => agency.Timezone)
            .FirstAsync(cancellationToken);

        var lineIds = order.Lines.Select(line => line.Id).ToList();
        var offerIds = order.Lines
            .Where(line => line.SupplierOfferId is not null)
            .Select(line => line.SupplierOfferId!.Value)
            .ToList();

        var travellers = await db.OrderTravellers
            .AsNoTracking()
            .Where(traveller => lineIds.Contains(traveller.OrderLineId))
            .OrderBy(traveller => traveller.Id)
            .Select(traveller => traveller.FirstName + " " + traveller.LastName)
            .ToListAsync(cancellationToken);

        // The first departure across the order, in the local time it was recorded in — the time on
        // the ticket, not a conversion of it.
        var flights = await db.FlightSegments
            .AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .Select(segment => segment.DepartureAt)
            .ToListAsync(cancellationToken);

        var buses = await db.BusSegments
            .AsNoTracking()
            .Where(segment => offerIds.Contains(segment.SupplierOfferId))
            .Select(segment => segment.DepartureAt)
            .ToListAsync(cancellationToken);

        var first = flights.Concat(buses).OrderBy(departure => departure.UtcDateTime).Cast<DateTimeOffset?>().FirstOrDefault();

        return new BookingDescription(
            order.AgencyId,
            order.OrderNumber,
            string.Join("; ", order.Lines.OrderBy(line => line.Id).Select(line => line.TitleSnapshot)),
            travellers.Count == 0 ? null : string.Join(", ", travellers.Distinct(StringComparer.Ordinal)),
            first?.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
            timeZoneId);
    }

    /// <summary>"Friday 2 October 2026 at 14:30 (Lagos time)" — the agency's wall clock, said plainly.</summary>
    private static string InAgencyTime(DateTimeOffset at, string timeZoneId)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        var place = timeZoneId[(timeZoneId.LastIndexOf('/') + 1)..].Replace('_', ' ');

        return $"{local.ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture)} ({place} time)";
    }

    private sealed record BookingDescription(
        Guid AgencyId,
        string Reference,
        string Itinerary,
        string? TravellerNames,
        string? DepartureDate,
        string TimeZoneId);
}
