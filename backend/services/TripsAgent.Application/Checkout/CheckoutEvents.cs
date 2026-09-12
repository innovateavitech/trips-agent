namespace TripsAgent.Application.Checkout;

// ============================================================================================
//  What the checkout announces, through the outbox — so each exists only if the change it
//  describes committed. These are the hooks for everything that follows a booking: the
//  traveller's emails, the invoice and the voucher.
// ============================================================================================

/// <summary>
/// A paid order line has its ticket, and its money has been taken: the booking is confirmed (#42).
/// </summary>
/// <remarks>
/// Hook the traveller's "booking confirmed" email, the invoice and the voucher here. Raised once per
/// line, after <c>BookingTicketed</c> has been turned into a captured payment.
/// </remarks>
public sealed record BookingConfirmed(
    Guid AgencyId,
    Guid OrderId,
    string OrderNumber,
    Guid OrderLineId,
    Guid SupplierBookingId,
    string? Pnr,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// A paid order line failed with the supplier and is waiting in the agent's resolution queue (#44).
/// </summary>
/// <remarks>
/// Hook the traveller's "needs attention" email here: one item needs attention, said honestly and
/// without alarm. Raised when a line is flagged — by a supplier reversal, or by a fare lapsing at
/// its ticket time limit after it was paid for.
/// </remarks>
public sealed record BookingNeedsResolution(
    Guid AgencyId,
    Guid OrderId,
    string OrderNumber,
    Guid OrderLineId,
    string Reason,
    DateTimeOffset FlaggedAt);

/// <summary>Money went back for an order line (#43, #44).</summary>
/// <param name="Method">How: <c>WalletHoldReleased</c>, <c>WalletCredited</c> or <c>Gateway</c>.</param>
public sealed record PaymentReversed(
    Guid AgencyId,
    Guid OrderId,
    Guid OrderLineId,
    Guid RefundId,
    string Method,
    long AmountMinor,
    string Currency,
    DateTimeOffset ReversedAt);

/// <summary>An agent decided what happens to a failed order line (#44).</summary>
/// <param name="Resolution"><c>ResolvedRefunded</c> today; <c>ResolvedRebooked</c> when rebooking arrives.</param>
public sealed record BookingResolved(
    Guid AgencyId,
    Guid OrderId,
    string OrderNumber,
    Guid OrderLineId,
    string Resolution,
    Guid ResolvedByUserId,
    DateTimeOffset ResolvedAt);
