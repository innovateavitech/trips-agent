using Microsoft.Extensions.Logging;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Payments;

namespace TripsAgent.Application.Notifications;

/// <summary>
/// The traveller's side of the booking pipeline's news (#42–#46), hooked to its outbox events: the
/// invoice, the vouchers and the emails that follow a confirmation, a failure and a refund.
/// </summary>
/// <remarks>
/// <para>
/// The Worker runs each from the event, acting as the event's agency. Each saves once, so an email and
/// a documents request commit together or not at all, and each is safe to run again: an event
/// delivered three times finds its email already queued and its documents already issued.
/// </para>
/// <para>
/// Only the traveller's side. The agency hears about the same moments from the pipeline itself — its
/// time limit warnings and expiries, its resolution queue — and nothing here repeats them.
/// </para>
/// </remarks>
public sealed partial class BookingFollowUps(
    IAppDbContext db,
    IBookingEmails emails,
    IBookingDocuments documents,
    ILogger<BookingFollowUps> logger)
{
    /// <summary>
    /// <see cref="BookingConfirmed"/>: the order's invoice and vouchers, emailed as PDFs once drawn, and
    /// "your booking is confirmed" for the line.
    /// </summary>
    /// <param name="confirmed">The event.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    public async Task ConfirmedAsync(BookingConfirmed confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        var customer = await CustomerAsync(confirmed.OrderLineId, cancellationToken);

        if (customer is null)
        {
            return;
        }

        // Asked for on every confirmed line. The Worker issues only what is missing, so an order's second
        // line gets its voucher without a second invoice.
        await documents.IssueForOrderAsync(confirmed.OrderId, customer, cancellationToken);
        await emails.SendBookingConfirmedAsync(confirmed.OrderLineId, customer, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary><see cref="BookingNeedsResolution"/>: "one item in your booking needs attention".</summary>
    /// <param name="flagged">The event.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    public async Task NeedsResolutionAsync(BookingNeedsResolution flagged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flagged);

        var customer = await CustomerAsync(flagged.OrderLineId, cancellationToken);

        if (customer is not null
            && await emails.SendItemNeedsAttentionAsync(flagged.OrderLineId, customer, flagged.FlaggedAt, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary><see cref="PaymentReversed"/>: the item was cancelled, and what happens to the money.</summary>
    /// <param name="reversed">The event.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is saved.</param>
    public async Task PaymentReversedAsync(PaymentReversed reversed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reversed);

        var customer = await CustomerAsync(reversed.OrderLineId, cancellationToken);

        if (customer is null)
        {
            return;
        }

        // A method this build does not know is described as a wallet refund: that wording names no
        // amount, and naming the wrong one is the mistake that matters.
        var method = Enum.TryParse<RefundMethod>(reversed.Method, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : RefundMethod.WalletCredited;

        var refund = new BookingRefund(reversed.RefundId, method, reversed.AmountMinor, reversed.Currency);

        if (await emails.SendRefundNoticeAsync(reversed.OrderLineId, customer, refund, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<BookingCustomer?> CustomerAsync(Guid orderLineId, CancellationToken cancellationToken)
    {
        var customer = await BookingCustomers.LeadTravellerAsync(db, orderLineId, cancellationToken);

        if (customer is null)
        {
            LogNoTraveller(logger, orderLineId);
        }

        return customer;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order line {OrderLineId} has no traveller to write to; nothing was sent for it.")]
    private static partial void LogNoTraveller(ILogger logger, Guid orderLineId);
}
